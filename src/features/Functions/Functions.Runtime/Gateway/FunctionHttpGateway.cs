using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Coordination;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Metering;
using Pole.Functions.Protocol;
using Pole.Observability;
using Pole.ResultType;

namespace Pole.Features.Functions.Runtime.Gateway;

public interface IFunctionHttpGateway
{
    Task InvokeAsync(
        HttpContext context,
        string functionRoute,
        string handlerPath,
        CancellationToken cancellationToken = default);
}

public sealed class FunctionHttpGateway(
    IFunctionRuntimeRouteResolver routeResolver,
    IFunctionEndpointRegistry endpointRegistry,
    IFunctionEndpointSelector endpointSelector,
    IFunctionResourcePlacementResolver placementResolver,
    IFunctionRevisionActivator activator,
    IFunctionAgentClient agentClient,
    IFunctionPendingAdmissionLimiter pendingAdmission,
    IFunctionPublicRateLimiter publicRateLimiter,
    IFunctionAdmissionCoordinator admissionCoordinator,
    IFunctionTrafficCoordinator trafficCoordinator,
    IFunctionMeterWriter meterWriter,
    FunctionGatewayOptions options,
    TimeProvider timeProvider) : IFunctionHttpGateway
{
    private static readonly HashSet<string> HopByHopHeaders = new(
        [
            "Connection",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade",
            "Proxy-Connection",
            "Content-Length"
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SensitiveRequestHeaders = new(
        [
            "Authorization",
            "Cookie",
            "Host",
            "X-Pole-Tenant-ID",
            "X-Forwarded-For",
            "X-Forwarded-Host",
            "X-Forwarded-Port",
            "X-Forwarded-Proto"
        ],
        StringComparer.OrdinalIgnoreCase);

    private sealed record FunctionHttpInvocation(
        string NormalizedRoute,
        string HandlerPath,
        FunctionExposure Exposure,
        string InvocationId,
        long ReservedBytes,
        ResolvedFunctionHttpRoute Resolved,
        FunctionRevisionCoordinationKey CoordinationKey,
        FunctionMeterDimensions MeterDimensions);

    private sealed record FunctionHttpAdmission(
        FunctionAdmissionLease Lease,
        bool RequiresActivationFence);

    private readonly record struct FunctionRequestBodyResult(bool Succeeded, byte[]? Body);

    private sealed record FunctionHttpAttempt(
        string AttemptId,
        DateTimeOffset DeadlineUtc,
        ReserveFunctionAttemptRequest ReserveRequest,
        HttpFunctionInvocationPayload Payload);

    private sealed record FunctionHttpDispatch(
        FunctionHttpInvocation Invocation,
        FunctionHttpAttempt Attempt,
        FunctionAdmissionLease DistributedAdmission,
        FunctionPendingAdmissionLease PendingAdmission,
        CancellationTokenSource ExecutionTimeout,
        CancellationToken ExecutionDeadline,
        CancellationToken PreStartDeadline,
        CancellationToken RequestCancellation);

    private enum EndpointReservationOutcome
    {
        Reserved,
        Handled,
        Unavailable,
        CapacityRejected
    }

    private enum EndpointExecutionOutcome
    {
        Handled,
        CapacityRejected
    }

    private readonly record struct EndpointReservationResult
    {
        private EndpointReservationResult(
            EndpointReservationOutcome outcome,
            string? reservationToken = null)
        {
            Outcome = outcome;
            ReservationToken = reservationToken;
        }

        public EndpointReservationOutcome Outcome { get; }

        public string? ReservationToken { get; }

        public static EndpointReservationResult Reserved(string reservationToken) =>
            new(EndpointReservationOutcome.Reserved, reservationToken);

        public static EndpointReservationResult Handled() =>
            new(EndpointReservationOutcome.Handled);

        public static EndpointReservationResult Unavailable() =>
            new(EndpointReservationOutcome.Unavailable);

        public static EndpointReservationResult CapacityRejected() =>
            new(EndpointReservationOutcome.CapacityRejected);
    }

    public async Task InvokeAsync(
        HttpContext context,
        string functionRoute,
        string handlerPath,
        CancellationToken cancellationToken = default)
    {
        var invocation = await ResolveInvocationAsync(
            context,
            functionRoute,
            handlerPath,
            cancellationToken);
        if (invocation is null ||
            !await TryAdmitPublicTrafficAsync(context, invocation, cancellationToken))
        {
            return;
        }

        using var pendingLease = await TryAcquirePendingAdmissionAsync(
            context,
            invocation,
            cancellationToken);
        if (pendingLease is null)
            return;

        var admission = await TryAcquireDistributedAdmissionAsync(
            context,
            invocation,
            cancellationToken);
        if (admission is null)
            return;

        await using var distributedAdmission = admission.Lease;
        using var admissionLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            distributedAdmission.LeaseLostToken);

        var body = await TryReadRequestBodyAsync(
            context,
            distributedAdmission,
            admissionLifetime.Token,
            cancellationToken);
        if (!body.Succeeded ||
            !await TryWriteAdmittedMeterAsync(
                context,
                invocation,
                distributedAdmission,
                admissionLifetime.Token,
                cancellationToken))
        {
            return;
        }

        var snapshot = await TryGetReadyEndpointsAsync(
            context,
            invocation,
            admission.RequiresActivationFence,
            distributedAdmission,
            admissionLifetime.Token,
            cancellationToken);
        if (snapshot is null)
            return;

        await DispatchToAgentsAsync(
            context,
            invocation,
            snapshot,
            body.Body,
            pendingLease,
            distributedAdmission,
            cancellationToken);
    }

    private async Task<FunctionHttpInvocation?> ResolveInvocationAsync(
        HttpContext context,
        string functionRoute,
        string handlerPath,
        CancellationToken cancellationToken)
    {
        var exposure = ResolveExposure(context.Connection.LocalPort);
        if (exposure is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "function_route_not_found",
                "The Function route was not found.", cancellationToken);
            return null;
        }

        var normalizedRoute = functionRoute.Trim().ToLowerInvariant();
        if (normalizedRoute.Length == 0)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "function_route_not_found",
                "The Function route was not found.", cancellationToken);
            return null;
        }

        if (!RequestMetadataIsWithinLimits(context.Request))
        {
            await WriteErrorAsync(context, StatusCodes.Status431RequestHeaderFieldsTooLarge,
                "request_metadata_too_large", "The Function request metadata exceeded its limit.", cancellationToken);
            return null;
        }

        if (context.Request.ContentLength is < 0 || context.Request.ContentLength > options.MaxRequestBodyBytes)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_body_too_large",
                "The Function request body exceeded its limit.", cancellationToken);
            return null;
        }

        var resolved = await routeResolver.ResolveAsync(
            normalizedRoute,
            handlerPath,
            context.Request.Method,
            exposure.Value,
            cancellationToken);
        if (resolved is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "function_route_not_found",
                "The Function route was not found.", cancellationToken);
            return null;
        }

        var invocationId = Guid.NewGuid().ToString("N");
        var reservedBytes = context.Request.ContentLength ?? options.MaxRequestBodyBytes;
        var coordinationKey = new FunctionRevisionCoordinationKey(
            resolved.App.TenantId,
            resolved.App.Id,
            resolved.Revision.Id);
        var meterDimensions = new FunctionMeterDimensions(
            resolved.App.TenantId,
            resolved.App.Id,
            resolved.Revision.Id,
            resolved.Route.TriggerId,
            resolved.Route.HandlerName,
            FunctionMeterTriggerKind.Http);
        return new FunctionHttpInvocation(
            normalizedRoute,
            handlerPath,
            exposure.Value,
            invocationId,
            reservedBytes,
            resolved,
            coordinationKey,
            meterDimensions);
    }

    private async Task<bool> TryAdmitPublicTrafficAsync(
        HttpContext context,
        FunctionHttpInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Exposure != FunctionExposure.Public)
            return true;

        if (!publicRateLimiter.TryAcquire(invocation.NormalizedRoute))
        {
            await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "rate_limit_exceeded",
                "The Function request rate limit was exceeded.", cancellationToken);
            return false;
        }

        FunctionTrafficDecision traffic;
        try
        {
            traffic = await trafficCoordinator.TryAcquireAsync(
                new FunctionAppCoordinationKey(
                    invocation.Resolved.App.TenantId,
                    invocation.Resolved.App.Id),
                new FunctionTrafficRequest(
                    invocation.InvocationId,
                    invocation.Resolved.Route.HandlerName),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                "function_coordination_unavailable",
                "Function request coordination is temporarily unavailable.", cancellationToken);
            return false;
        }

        if (traffic.Kind == FunctionTrafficDecisionKind.Granted)
            return true;

        if (traffic.RetryAfterMilliseconds > 0)
            context.Response.Headers.RetryAfter = RetryAfterSeconds(traffic.RetryAfterMilliseconds);
        await WriteErrorAsync(
            context,
            traffic.Rejection == FunctionTrafficRejection.RateLimitExceeded
                ? StatusCodes.Status429TooManyRequests
                : StatusCodes.Status503ServiceUnavailable,
            traffic.Rejection == FunctionTrafficRejection.RateLimitExceeded
                ? "rate_limit_exceeded"
                : "function_coordination_rejected",
            traffic.Rejection == FunctionTrafficRejection.RateLimitExceeded
                ? "The Function request rate limit was exceeded."
                : "Function request coordination rejected an invalid decision.",
            cancellationToken);
        return false;
    }

    private async Task<FunctionPendingAdmissionLease?> TryAcquirePendingAdmissionAsync(
        HttpContext context,
        FunctionHttpInvocation invocation,
        CancellationToken cancellationToken)
    {
        var pendingKey =
            $"{invocation.Resolved.App.TenantId}/{invocation.Resolved.App.Id}/{invocation.Resolved.Revision.Id}";
        var lease = pendingAdmission.TryAcquire(
            pendingKey,
            invocation.Resolved.Route.HandlerName,
            invocation.ReservedBytes);
        if (lease is not null)
            return lease;

        await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "pending_limit_exceeded",
            "The Function pending request limit was exceeded.", cancellationToken);
        return null;
    }

    private async Task<FunctionHttpAdmission?> TryAcquireDistributedAdmissionAsync(
        HttpContext context,
        FunctionHttpInvocation invocation,
        CancellationToken cancellationToken)
    {
        FunctionAdmissionDecision admissionDecision;
        try
        {
            admissionDecision = await admissionCoordinator.AcquireAsync(
                invocation.CoordinationKey,
                new FunctionAdmissionRequest(
                    $"http:{invocation.InvocationId}",
                    invocation.InvocationId,
                    invocation.Resolved.Route.HandlerName,
                    FunctionAdmissionTriggerKind.Http,
                    invocation.ReservedBytes),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                "function_coordination_unavailable",
                "Function request coordination is temporarily unavailable.", cancellationToken);
            return null;
        }

        if (admissionDecision.Kind != FunctionAdmissionDecisionKind.Granted ||
            admissionDecision.Token is not { } admissionToken)
        {
            if (admissionDecision.RetryAfterMilliseconds > 0)
                context.Response.Headers.RetryAfter = RetryAfterSeconds(admissionDecision.RetryAfterMilliseconds);
            var quiescing = admissionDecision.Kind == FunctionAdmissionDecisionKind.Rejected &&
                            admissionDecision.Rejection == FunctionAdmissionRejection.RevisionQuiescing;
            var capacityExceeded = admissionDecision.Kind == FunctionAdmissionDecisionKind.Rejected &&
                                   admissionDecision.Rejection is
                                       FunctionAdmissionRejection.RequestLimitExceeded or
                                       FunctionAdmissionRejection.ByteLimitExceeded;
            await WriteErrorAsync(
                context,
                quiescing
                    ? StatusCodes.Status503ServiceUnavailable
                    : capacityExceeded
                        ? StatusCodes.Status429TooManyRequests
                        : StatusCodes.Status503ServiceUnavailable,
                quiescing
                    ? "revision_scaling"
                    : capacityExceeded
                        ? "pending_limit_exceeded"
                        : "function_coordination_rejected",
                quiescing
                    ? "The Function revision is completing a scaling transition."
                    : capacityExceeded
                        ? "The Function pending request limit was exceeded."
                        : "Function request coordination returned an invalid decision.",
                cancellationToken);
            return null;
        }

        return new FunctionHttpAdmission(
            new FunctionAdmissionLease(
                admissionCoordinator,
                invocation.CoordinationKey,
                admissionToken,
                timeProvider),
            admissionDecision.RequiresActivationFence);
    }

    private async Task<FunctionRequestBodyResult> TryReadRequestBodyAsync(
        HttpContext context,
        FunctionAdmissionLease distributedAdmission,
        CancellationToken admissionLifetime,
        CancellationToken cancellationToken)
    {
        using var bodyTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(options.RequestBodyTimeoutSeconds),
            timeProvider);
        using var bodyLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            admissionLifetime,
            bodyTimeout.Token);

        try
        {
            return new FunctionRequestBodyResult(
                true,
                await ReadBodyAsync(context.Request, bodyLifetime.Token));
        }
        catch (FunctionRequestBodyTooLargeException)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_body_too_large",
                "The Function request body exceeded its limit.", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller disconnected; there is no response to write.
        }
        catch (OperationCanceledException) when (distributedAdmission.IsLost)
        {
            await WriteAdmissionLostAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (bodyTimeout.IsCancellationRequested)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status408RequestTimeout,
                "request_body_timeout",
                "The Function request body was not received before the upload deadline.",
                cancellationToken);
        }

        return default;
    }

    private async Task<bool> TryWriteAdmittedMeterAsync(
        HttpContext context,
        FunctionHttpInvocation invocation,
        FunctionAdmissionLease distributedAdmission,
        CancellationToken admissionLifetime,
        CancellationToken cancellationToken)
    {
        try
        {
            await meterWriter.WriteAsync(
                FunctionMeterEvent.CreateFact(
                    invocation.MeterDimensions,
                    invocation.InvocationId,
                    attemptNumber: 1,
                    FunctionMeterEventPhase.Admitted,
                    outcome: null,
                    executionDurationMilliseconds: null,
                    timeProvider.GetUtcNow()),
                admissionLifetime);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) when (distributedAdmission.IsLost)
        {
            await WriteAdmissionLostAsync(context, cancellationToken);
            return false;
        }
    }

    private async Task<FunctionEndpointSnapshot?> TryGetReadyEndpointsAsync(
        HttpContext context,
        FunctionHttpInvocation invocation,
        bool requiresActivationFence,
        FunctionAdmissionLease distributedAdmission,
        CancellationToken admissionLifetime,
        CancellationToken cancellationToken)
    {
        var resolvedPlacement = await placementResolver.ResolveAsync(
            invocation.Resolved.App,
            cancellationToken: admissionLifetime);
        if (!resolvedPlacement.TryGetValue(out var placement, out _))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                "function_placement_unavailable",
                "The Function placement is temporarily unavailable.",
                cancellationToken);
            return null;
        }

        var subject = new FunctionRevisionInKubernetes(
            invocation.Resolved.App,
            invocation.Resolved.Revision,
            placement!);
        var service = new FunctionServiceKey(
            placement!.DeploymentTargetId,
            subject.Namespace,
            subject.ServiceName);
        var snapshot = endpointRegistry.GetSnapshot(service);
        if (snapshot.HasReadyEndpoints && !requiresActivationFence)
            return snapshot;

        try
        {
            var activation = await activator.EnsureActiveAsync(
                subject,
                admissionLifetime,
                forceScaleFence: requiresActivationFence);
            if (activation.TryGetValue(out snapshot, out _))
                return snapshot;

            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "activation_failed",
                "The Function revision could not be activated.", cancellationToken);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException) when (distributedAdmission.IsLost)
        {
            await WriteAdmissionLostAsync(context, cancellationToken);
            return null;
        }
    }

    private async Task DispatchToAgentsAsync(
        HttpContext context,
        FunctionHttpInvocation invocation,
        FunctionEndpointSnapshot snapshot,
        byte[]? body,
        FunctionPendingAdmissionLease pendingLease,
        FunctionAdmissionLease distributedAdmission,
        CancellationToken cancellationToken)
    {
        var attempt = CreateAttempt(context, invocation, body);
        using var executionTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(options.ExecutionTimeoutSeconds),
            timeProvider);
        using var executionDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            executionTimeout.Token);
        using var preStartDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            executionDeadline.Token,
            distributedAdmission.LeaseLostToken);
        var dispatch = new FunctionHttpDispatch(
            invocation,
            attempt,
            distributedAdmission,
            pendingLease,
            executionTimeout,
            executionDeadline.Token,
            preStartDeadline.Token,
            cancellationToken);

        var endpointOrder = endpointSelector.Order(snapshot);
        if (endpointOrder.Count == 0)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "no_ready_agents",
                "The Function revision has no ready Agent endpoints.", cancellationToken);
            return;
        }

        var hadCapacityRejection = false;
        foreach (var endpoint in endpointOrder)
        {
            var reservation = await TryReserveEndpointAsync(context, endpoint, dispatch);
            if (reservation.Outcome == EndpointReservationOutcome.Handled)
                return;
            if (reservation.Outcome == EndpointReservationOutcome.Unavailable)
                continue;
            if (reservation.Outcome == EndpointReservationOutcome.CapacityRejected)
            {
                hadCapacityRejection = true;
                continue;
            }

            var reservationToken = reservation.ReservationToken
                                   ?? throw new InvalidOperationException(
                                       "A successful Function Agent reservation must include its token.");
            var outcome = await TryExecuteEndpointAsync(
                context,
                endpoint,
                reservationToken,
                dispatch);
            if (outcome == EndpointExecutionOutcome.Handled)
                return;
            if (outcome == EndpointExecutionOutcome.CapacityRejected)
            {
                hadCapacityRejection = true;
                continue;
            }
        }

        await WriteErrorAsync(
            context,
            hadCapacityRejection ? StatusCodes.Status429TooManyRequests : StatusCodes.Status503ServiceUnavailable,
            hadCapacityRejection ? PoleFunctionProtocolErrors.CapacityExhausted : "agents_unavailable",
            hadCapacityRejection
                ? "All Function Agent instances are at capacity."
                : "No Function Agent instance could accept the request.",
            cancellationToken);
    }

    private FunctionHttpAttempt CreateAttempt(
        HttpContext context,
        FunctionHttpInvocation invocation,
        byte[]? body)
    {
        var attemptId = Guid.NewGuid().ToString("N");
        var deadlineUtc = timeProvider.GetUtcNow().AddSeconds(options.ExecutionTimeoutSeconds);
        var identity = new FunctionAttemptIdentity(
            invocation.Resolved.App.TenantId,
            invocation.Resolved.App.Id,
            invocation.Resolved.Revision.Id,
            invocation.Resolved.Route.HandlerName,
            FunctionTriggerKind.Http,
            invocation.InvocationId,
            attemptId,
            AttemptNumber: 1);
        var traceContext = new FunctionInvocationTraceContext(
            Activity.Current?.Id,
            Activity.Current?.TraceStateString,
            TraceContext.CurrentCorrelationId);
        var reserveRequest = new ReserveFunctionAttemptRequest(
            PoleFunctionProtocol.Version,
            identity,
            deadlineUtc,
            traceContext);
        var payload = new HttpFunctionInvocationPayload(
            context.Request.Method,
            NormalizeHandlerPath(invocation.HandlerPath),
            context.Request.QueryString.Value ?? string.Empty,
            ForwardHeaders(context.Request.Headers),
            body);
        return new FunctionHttpAttempt(attemptId, deadlineUtc, reserveRequest, payload);
    }

    private async Task<EndpointReservationResult> TryReserveEndpointAsync(
        HttpContext context,
        FunctionEndpoint endpoint,
        FunctionHttpDispatch dispatch)
    {
        FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse> reservation;
        try
        {
            reservation = await agentClient.ReserveAsync(
                endpoint,
                dispatch.Invocation.Resolved.App,
                dispatch.Invocation.Resolved.Revision,
                dispatch.Attempt.ReserveRequest,
                dispatch.PreStartDeadline);
        }
        catch (OperationCanceledException) when (dispatch.RequestCancellation.IsCancellationRequested)
        {
            return EndpointReservationResult.Handled();
        }
        catch (OperationCanceledException) when (dispatch.ExecutionTimeout.IsCancellationRequested)
        {
            await WriteErrorAsync(context, StatusCodes.Status504GatewayTimeout, "execution_deadline_exceeded",
                "The Function execution deadline elapsed.", context.RequestAborted);
            return EndpointReservationResult.Handled();
        }
        catch (OperationCanceledException) when (dispatch.DistributedAdmission.IsLost)
        {
            await WriteAdmissionLostAsync(context, dispatch.RequestCancellation);
            return EndpointReservationResult.Handled();
        }
        catch (Exception exception) when (IsPreStartTransportFailure(exception, dispatch.PreStartDeadline))
        {
            return EndpointReservationResult.Unavailable();
        }

        if (!reservation.IsSuccess || reservation.StatusCode != HttpStatusCode.OK)
        {
            if (IsPreStartRejection(reservation.Error))
                return EndpointReservationResult.CapacityRejected();

            await WriteAgentErrorAsync(context, reservation.Error, dispatch.RequestCancellation);
            return EndpointReservationResult.Handled();
        }
        if (reservation.Value is null ||
            !string.Equals(
                reservation.Value.AttemptId,
                dispatch.Attempt.AttemptId,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(reservation.Value.ReservationToken))
        {
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "agent_attempt_mismatch",
                "The Function Agent returned a different attempt identity.", dispatch.RequestCancellation);
            return EndpointReservationResult.Handled();
        }
        if (reservation.Value.ExpiresAtUtc <= timeProvider.GetUtcNow() ||
            reservation.Value.ExpiresAtUtc > dispatch.Attempt.DeadlineUtc)
        {
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "invalid_agent_response",
                "The Function Agent returned an invalid reservation.", dispatch.RequestCancellation);
            return EndpointReservationResult.Handled();
        }

        return EndpointReservationResult.Reserved(reservation.Value.ReservationToken);
    }

    private async Task<EndpointExecutionOutcome> TryExecuteEndpointAsync(
        HttpContext context,
        FunctionEndpoint endpoint,
        string reservationToken,
        FunctionHttpDispatch dispatch)
    {
        if (dispatch.DistributedAdmission.IsLost)
        {
            await WriteAdmissionLostAsync(context, dispatch.RequestCancellation);
            return EndpointExecutionOutcome.Handled;
        }

        FunctionAgentProtocolResponse<StartFunctionAttemptResponse> started;
        try
        {
            started = await agentClient.ExecuteAsync(
                endpoint,
                dispatch.Invocation.Resolved.App,
                dispatch.Invocation.Resolved.Revision,
                dispatch.Attempt.AttemptId,
                new ExecuteFunctionAttemptRequest(
                    PoleFunctionProtocol.Version,
                    reservationToken,
                    dispatch.Attempt.Payload),
                dispatch.PreStartDeadline);
        }
        catch (OperationCanceledException) when (
            dispatch.DistributedAdmission.IsLost &&
            !dispatch.RequestCancellation.IsCancellationRequested &&
            !dispatch.ExecutionTimeout.IsCancellationRequested)
        {
            // Execute may have crossed the Agent boundary. Reconcile on that same Agent
            // without tying the observation to the now-lost admission heartbeat.
            await ReleaseAndObserveAmbiguousAttemptAsync(context, endpoint, dispatch);
            return EndpointExecutionOutcome.Handled;
        }
        catch (Exception exception) when (IsTransportFailure(exception))
        {
            // Execute may have reached the Agent. Reconcile only with that Agent; never reroute.
            await ReleaseAndObserveAmbiguousAttemptAsync(context, endpoint, dispatch);
            return EndpointExecutionOutcome.Handled;
        }

        if (!started.IsSuccess || started.StatusCode != HttpStatusCode.Accepted)
        {
            if (IsPreStartRejection(started.Error))
                return EndpointExecutionOutcome.CapacityRejected;

            await WriteAgentErrorAsync(context, started.Error, dispatch.RequestCancellation);
            return EndpointExecutionOutcome.Handled;
        }

        await ReleaseAdmissionBestEffortAsync(dispatch.DistributedAdmission);
        dispatch.PendingAdmission.Dispose();
        if (started.Value is null ||
            !string.Equals(started.Value.AttemptId, dispatch.Attempt.AttemptId, StringComparison.Ordinal))
        {
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "agent_attempt_mismatch",
                "The Function Agent returned a different attempt identity.", dispatch.RequestCancellation);
            return EndpointExecutionOutcome.Handled;
        }

        await TryWritePostStartMeterAsync(
            FunctionMeterEvent.CreateFact(
                dispatch.Invocation.MeterDimensions,
                dispatch.Invocation.InvocationId,
                attemptNumber: 1,
                FunctionMeterEventPhase.Started,
                outcome: null,
                executionDurationMilliseconds: null,
                started.Value.StartedAtUtc));
        await ObserveAndWriteAsync(
            context,
            endpoint,
            dispatch.Invocation.Resolved,
            dispatch.Attempt.AttemptId,
            dispatch.Attempt.DeadlineUtc,
            dispatch.Invocation.MeterDimensions,
            dispatch.Invocation.InvocationId,
            startedMeterWritten: true,
            dispatch.ExecutionDeadline);
        return EndpointExecutionOutcome.Handled;
    }

    private async Task ReleaseAndObserveAmbiguousAttemptAsync(
        HttpContext context,
        FunctionEndpoint endpoint,
        FunctionHttpDispatch dispatch)
    {
        await ReleaseAdmissionBestEffortAsync(dispatch.DistributedAdmission);
        dispatch.PendingAdmission.Dispose();
        await ObserveAndWriteAsync(
            context,
            endpoint,
            dispatch.Invocation.Resolved,
            dispatch.Attempt.AttemptId,
            dispatch.Attempt.DeadlineUtc,
            dispatch.Invocation.MeterDimensions,
            dispatch.Invocation.InvocationId,
            startedMeterWritten: false,
            dispatch.ExecutionDeadline);
    }

    private async Task ObserveAndWriteAsync(
        HttpContext context,
        FunctionEndpoint endpoint,
        ResolvedFunctionHttpRoute resolved,
        string attemptId,
        DateTimeOffset deadlineUtc,
        FunctionMeterDimensions meterDimensions,
        string invocationId,
        bool startedMeterWritten,
        CancellationToken cancellationToken)
    {
        var consecutiveTransportFailures = 0;
        while (timeProvider.GetUtcNow() < deadlineUtc)
        {
            var remaining = deadlineUtc - timeProvider.GetUtcNow();
            var waitMilliseconds = (int)Math.Clamp(
                remaining.TotalMilliseconds,
                0,
                options.AgentLongPollMilliseconds);
            FunctionAgentProtocolResponse<FunctionAttemptStatusResponse> observation;
            try
            {
                observation = await agentClient.ObserveAsync(
                    endpoint,
                    resolved.App,
                    resolved.Revision,
                    attemptId,
                    waitMilliseconds,
                    cancellationToken);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                consecutiveTransportFailures++;
                var backoff = TimeSpan.FromMilliseconds(Math.Min(
                    1000,
                    100 * Math.Pow(2, Math.Min(consecutiveTransportFailures - 1, 4))));
                try
                {
                    await Task.Delay(backoff, timeProvider, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            consecutiveTransportFailures = 0;
            if (!observation.IsSuccess || observation.StatusCode != HttpStatusCode.OK ||
                observation.Value?.Status is null)
            {
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "attempt_reconciliation_failed",
                    "The Function attempt could not be reconciled.",
                    context.RequestAborted);
                return;
            }
            if (!string.Equals(observation.Value.AgentInstanceId, endpoint.TargetUid, StringComparison.Ordinal))
            {
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "agent_identity_mismatch",
                    "The Function Agent identity did not match the selected endpoint.", context.RequestAborted);
                return;
            }
            if (!string.Equals(observation.Value.Status.AttemptId, attemptId, StringComparison.Ordinal))
            {
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "agent_attempt_mismatch",
                    "The Function Agent returned a different attempt identity.", context.RequestAborted);
                return;
            }

            switch (observation.Value.Status)
            {
                case CompletedFunctionAttemptStatus completed:
                    if (!startedMeterWritten)
                    {
                        await TryWritePostStartMeterAsync(
                            FunctionMeterEvent.CreateFact(
                                meterDimensions,
                                invocationId,
                                attemptNumber: 1,
                                FunctionMeterEventPhase.Started,
                                outcome: null,
                                executionDurationMilliseconds: null,
                                completed.StartedAtUtc));
                        startedMeterWritten = true;
                    }
                    await WriteCompletionMeterAsync(
                        meterDimensions,
                        invocationId,
                        completed);
                    await WriteCompletionAsync(context, completed, context.RequestAborted);
                    return;
                case ExpiredFunctionAttemptStatus:
                case UnknownFunctionAttemptStatus:
                    await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "attempt_state_unknown",
                        "The Function attempt state is unknown; it was not executed again.", context.RequestAborted);
                    return;
                case ReservedFunctionAttemptStatus:
                    break;
                case StartedFunctionAttemptStatus started:
                    if (!startedMeterWritten)
                    {
                        await TryWritePostStartMeterAsync(
                            FunctionMeterEvent.CreateFact(
                                meterDimensions,
                                invocationId,
                                attemptNumber: 1,
                                FunctionMeterEventPhase.Started,
                                outcome: null,
                                executionDurationMilliseconds: null,
                                started.StartedAtUtc));
                        startedMeterWritten = true;
                    }
                    break;
            }
        }

        if (!context.RequestAborted.IsCancellationRequested)
        {
            await WriteErrorAsync(context, StatusCodes.Status504GatewayTimeout, "execution_deadline_exceeded",
                "The Function execution deadline elapsed.", context.RequestAborted);
        }
    }

    private Task WriteCompletionMeterAsync(
        FunctionMeterDimensions dimensions,
        string invocationId,
        CompletedFunctionAttemptStatus completed)
    {
        var outcome = completed.Outcome switch
        {
            FunctionAttemptOutcome.Succeeded => FunctionMeterOutcome.Succeeded,
            FunctionAttemptOutcome.RetryableFailure => FunctionMeterOutcome.RetryableFailure,
            FunctionAttemptOutcome.PermanentFailure => FunctionMeterOutcome.PermanentFailure,
            FunctionAttemptOutcome.TimedOut => FunctionMeterOutcome.TimedOut,
            FunctionAttemptOutcome.Cancelled => FunctionMeterOutcome.Cancelled,
            _ => FunctionMeterOutcome.PlatformFailure
        };
        var duration = Math.Max(
            0,
            (long)(completed.CompletedAtUtc - completed.StartedAtUtc).TotalMilliseconds);
        return TryWritePostStartMeterAsync(FunctionMeterEvent.CreateFact(
                dimensions,
                invocationId,
                attemptNumber: 1,
                FunctionMeterEventPhase.Completed,
                outcome,
                duration,
                completed.CompletedAtUtc));
    }

    private async Task ReleaseAdmissionBestEffortAsync(FunctionAdmissionLease admission)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2), timeProvider);
        try
        {
            await admission.ReleaseAsync(timeout.Token);
        }
        catch
        {
            // Lease expiry is the crash/unavailability fallback. Agent and durable state become
            // the demand source as soon as Execute may have crossed the start boundary.
        }
    }

    private static Task WriteAdmissionLostAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        WriteErrorAsync(
            context,
            StatusCodes.Status503ServiceUnavailable,
            "function_coordination_lost",
            "Function request coordination was lost before execution started.",
            cancellationToken);

    private static string RetryAfterSeconds(int milliseconds) =>
        Math.Max(1, (int)Math.Ceiling(milliseconds / 1000d)).ToString();

    private async Task TryWritePostStartMeterAsync(FunctionMeterEvent meterEvent)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(options.PostStartMeterWriteTimeoutMilliseconds),
            timeProvider);
        await meterWriter.TryWriteAsync(meterEvent, timeout.Token);
    }

    private async Task WriteCompletionAsync(
        HttpContext context,
        CompletedFunctionAttemptStatus completed,
        CancellationToken cancellationToken)
    {
        if (completed.Outcome == FunctionAttemptOutcome.Succeeded &&
            completed.Result is HttpFunctionInvocationResult httpResult &&
            httpResult.Headers is not null)
        {
            if (httpResult.StatusCode is < 200 or > 599 ||
                httpResult.Body is { Length: var bodyLength } && bodyLength > options.MaxResponseBodyBytes ||
                !ResponseMetadataIsWithinLimits(httpResult.Headers))
            {
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "invalid_handler_response",
                    "The Function handler response metadata exceeded its limit.", cancellationToken);
                return;
            }

            context.Response.StatusCode = httpResult.StatusCode;
            var connectionHeaders = ConnectionNominatedHeaders(httpResult.Headers);
            foreach (var header in httpResult.Headers)
            {
                if (HopByHopHeaders.Contains(header.Name) || connectionHeaders.Contains(header.Name))
                    continue;
                context.Response.Headers.Append(header.Name, new StringValues(header.Values.ToArray()));
            }
            if (!HttpMethods.IsHead(context.Request.Method) &&
                httpResult.StatusCode is not StatusCodes.Status204NoContent and not StatusCodes.Status304NotModified &&
                httpResult.Body is { Length: > 0 })
                await context.Response.Body.WriteAsync(httpResult.Body, cancellationToken);
            return;
        }

        if (completed.Outcome == FunctionAttemptOutcome.Succeeded)
        {
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "invalid_handler_response",
                "The Function Agent returned an invalid handler result.", cancellationToken);
            return;
        }

        var (statusCode, code) = completed.Outcome switch
        {
            FunctionAttemptOutcome.TimedOut => (StatusCodes.Status504GatewayTimeout, "execution_deadline_exceeded"),
            FunctionAttemptOutcome.Cancelled => (StatusCodes.Status502BadGateway, "function_cancelled"),
            FunctionAttemptOutcome.PlatformFailure => (StatusCodes.Status502BadGateway, "function_runtime_failed"),
            _ => (StatusCodes.Status500InternalServerError, "function_failed")
        };
        await WriteErrorAsync(
            context,
            statusCode,
            completed.Failure?.Code ?? code,
            completed.Failure?.Message ?? "The Function invocation failed.",
            cancellationToken);
    }

    private bool RequestMetadataIsWithinLimits(HttpRequest request)
    {
        if (request.Headers.Count > options.MaxHeaderCount)
            return false;
        long headerBytes = 0;
        foreach (var (name, values) in request.Headers)
        {
            headerBytes += Encoding.UTF8.GetByteCount(name);
            foreach (var value in values)
                headerBytes += Encoding.UTF8.GetByteCount(value ?? string.Empty);
            if (headerBytes > options.MaxHeaderBytes)
                return false;
        }

        var pathAndQuery = (request.Path.Value ?? string.Empty) + request.QueryString.Value;
        return Encoding.UTF8.GetByteCount(pathAndQuery) <= options.MaxPathAndQueryBytes;
    }

    private bool ResponseMetadataIsWithinLimits(IReadOnlyList<FunctionHttpHeader> headers)
    {
        if (headers.Count > options.MaxHeaderCount)
            return false;
        long bytes = 0;
        foreach (var header in headers)
        {
            if (!HeaderIsSafe(header))
                return false;
            bytes += Encoding.UTF8.GetByteCount(header.Name);
            foreach (var value in header.Values)
                bytes += Encoding.UTF8.GetByteCount(value);
            if (bytes > options.MaxHeaderBytes)
                return false;
        }
        return true;
    }

    private async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength == 0)
            return null;
        using var buffer = new MemoryStream(
            request.ContentLength is > 0 and <= int.MaxValue ? (int)request.ContentLength.Value : 0);
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > options.MaxRequestBodyBytes)
                throw new FunctionRequestBodyTooLargeException();
            buffer.Write(chunk, 0, read);
        }
        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    private IReadOnlyList<FunctionHttpHeader> ForwardHeaders(IHeaderDictionary headers)
    {
        var connectionHeaders = headers.Connection
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return headers
            .Where(header =>
                options.ForwardedRequestHeaders.Contains(header.Key) &&
                !SensitiveRequestHeaders.Contains(header.Key) &&
                !HopByHopHeaders.Contains(header.Key) &&
                !connectionHeaders.Contains(header.Key))
            .Select(header => new FunctionHttpHeader(
                header.Key,
                header.Value.Select(value => value ?? string.Empty).ToArray()))
            .ToArray();
    }

    private FunctionExposure? ResolveExposure(int localPort) => localPort switch
    {
        var port when port == options.PublicPort => FunctionExposure.Public,
        var port when port == options.InternalPort => FunctionExposure.Internal,
        _ => null
    };

    private static string NormalizeHandlerPath(string path) =>
        "/" + path.Trim('/');

    private static bool IsPreStartRejection(FunctionProtocolError? error) =>
        error?.Code is PoleFunctionProtocolErrors.CapacityExhausted or PoleFunctionProtocolErrors.AgentDraining;

    private static bool IsPreStartTransportFailure(Exception exception, CancellationToken deadlineToken) =>
        IsTransportFailure(exception) && !deadlineToken.IsCancellationRequested;

    private static bool IsTransportFailure(Exception exception) =>
        exception is HttpRequestException or FunctionAgentProtocolException or OperationCanceledException;

    private static bool HeaderIsSafe(FunctionHttpHeader header) =>
        !string.IsNullOrWhiteSpace(header.Name) &&
        header.Name.All(IsHeaderTokenCharacter) &&
        header.Values is not null &&
        header.Values.All(value => value is not null && !value.Contains('\r') && !value.Contains('\n'));

    private static bool IsHeaderTokenCharacter(char character) =>
        character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or
            '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private static HashSet<string> ConnectionNominatedHeaders(IReadOnlyList<FunctionHttpHeader> headers) =>
        headers
            .Where(header => string.Equals(header.Name, "Connection", StringComparison.OrdinalIgnoreCase) &&
                             header.Values is not null)
            .SelectMany(header => header.Values)
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Task WriteAgentErrorAsync(
        HttpContext context,
        FunctionProtocolError? error,
        CancellationToken cancellationToken) =>
        WriteErrorAsync(
            context,
            StatusCodes.Status502BadGateway,
            error?.Code ?? "invalid_agent_response",
            "The Function Agent rejected the request.",
            cancellationToken);

    private static Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        if (statusCode == StatusCodes.Status429TooManyRequests &&
            !context.Response.Headers.ContainsKey("Retry-After"))
            context.Response.Headers.RetryAfter = "1";
        return context.Response.WriteAsJsonAsync(new FunctionGatewayError(code, message), cancellationToken);
    }
}

public sealed record FunctionGatewayError(string Code, string Message);

internal sealed class FunctionRequestBodyTooLargeException : Exception;
