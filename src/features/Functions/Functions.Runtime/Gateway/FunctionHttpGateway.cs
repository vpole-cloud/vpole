using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Functions.Protocol;
using Pole.Observability;

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
    IFunctionRevisionActivator activator,
    IFunctionAgentClient agentClient,
    IFunctionPendingAdmissionLimiter pendingAdmission,
    IFunctionPublicRateLimiter publicRateLimiter,
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

    public async Task InvokeAsync(
        HttpContext context,
        string functionRoute,
        string handlerPath,
        CancellationToken cancellationToken = default)
    {
        var exposure = ResolveExposure(context.Connection.LocalPort);
        if (exposure is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "function_route_not_found",
                "The Function route was not found.", cancellationToken);
            return;
        }

        var normalizedRoute = functionRoute.Trim().ToLowerInvariant();
        if (normalizedRoute.Length == 0)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, "function_route_not_found",
                "The Function route was not found.", cancellationToken);
            return;
        }

        if (!RequestMetadataIsWithinLimits(context.Request))
        {
            await WriteErrorAsync(context, StatusCodes.Status431RequestHeaderFieldsTooLarge,
                "request_metadata_too_large", "The Function request metadata exceeded its limit.", cancellationToken);
            return;
        }

        if (context.Request.ContentLength is < 0 || context.Request.ContentLength > options.MaxRequestBodyBytes)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_body_too_large",
                "The Function request body exceeded its limit.", cancellationToken);
            return;
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
            return;
        }

        if (exposure == FunctionExposure.Public && !publicRateLimiter.TryAcquire(normalizedRoute))
        {
            await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "rate_limit_exceeded",
                "The Function request rate limit was exceeded.", cancellationToken);
            return;
        }

        var reservedBytes = context.Request.ContentLength ?? options.MaxRequestBodyBytes;
        var pendingKey = $"{resolved.App.TenantId}/{resolved.App.Id}/{resolved.Revision.Id}";
        using var pendingLease = pendingAdmission.TryAcquire(pendingKey, reservedBytes);
        if (pendingLease is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "pending_limit_exceeded",
                "The Function pending request limit was exceeded.", cancellationToken);
            return;
        }

        byte[]? body;
        try
        {
            body = await ReadBodyAsync(context.Request, cancellationToken);
        }
        catch (FunctionRequestBodyTooLargeException)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_body_too_large",
                "The Function request body exceeded its limit.", cancellationToken);
            return;
        }

        var subject = new FunctionRevisionInKubernetes(resolved.App, resolved.Revision);
        var service = new FunctionServiceKey(subject.Namespace, subject.ServiceName);
        var snapshot = endpointRegistry.GetSnapshot(service);
        if (!snapshot.HasReadyEndpoints)
        {
            var activation = await activator.EnsureActiveAsync(
                resolved.App,
                resolved.Revision,
                cancellationToken);
            if (!activation.TryGetValue(out snapshot, out _))
            {
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "activation_failed",
                    "The Function revision could not be activated.", cancellationToken);
                return;
            }
        }

        var invocationId = Guid.NewGuid().ToString("N");
        var attemptId = Guid.NewGuid().ToString("N");
        var deadlineUtc = timeProvider.GetUtcNow().AddSeconds(options.ExecutionTimeoutSeconds);
        var identity = new FunctionAttemptIdentity(
            resolved.App.TenantId,
            resolved.App.Id,
            resolved.Revision.Id,
            resolved.Route.HandlerName,
            FunctionTriggerKind.Http,
            invocationId,
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
            NormalizeHandlerPath(handlerPath),
            context.Request.QueryString.Value ?? string.Empty,
            ForwardHeaders(context.Request.Headers),
            body);

        using var executionTimeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(options.ExecutionTimeoutSeconds),
            timeProvider);
        using var executionDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            executionTimeout.Token);
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
            FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse> reservation;
            try
            {
                reservation = await agentClient.ReserveAsync(
                    endpoint,
                    resolved.App,
                    resolved.Revision,
                    reserveRequest,
                    executionDeadline.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (executionTimeout.IsCancellationRequested)
            {
                await WriteErrorAsync(context, StatusCodes.Status504GatewayTimeout, "execution_deadline_exceeded",
                    "The Function execution deadline elapsed.", context.RequestAborted);
                return;
            }
            catch (Exception exception) when (IsPreStartTransportFailure(exception, executionDeadline.Token))
            {
                continue;
            }

            if (!reservation.IsSuccess || reservation.StatusCode != HttpStatusCode.OK)
            {
                if (IsPreStartRejection(reservation.Error))
                {
                    hadCapacityRejection = true;
                    continue;
                }

                await WriteAgentErrorAsync(context, reservation.Error, cancellationToken);
                return;
            }
            if (reservation.Value is null ||
                !string.Equals(reservation.Value.AttemptId, attemptId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(reservation.Value.ReservationToken))
            {
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "agent_attempt_mismatch",
                    "The Function Agent returned a different attempt identity.", cancellationToken);
                return;
            }
            if (reservation.Value.ExpiresAtUtc <= timeProvider.GetUtcNow() ||
                reservation.Value.ExpiresAtUtc > deadlineUtc)
            {
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "invalid_agent_response",
                    "The Function Agent returned an invalid reservation.", cancellationToken);
                return;
            }

            FunctionAgentProtocolResponse<StartFunctionAttemptResponse> started;
            try
            {
                started = await agentClient.ExecuteAsync(
                    endpoint,
                    resolved.App,
                    resolved.Revision,
                    attemptId,
                    new ExecuteFunctionAttemptRequest(
                        PoleFunctionProtocol.Version,
                        reservation.Value.ReservationToken,
                        payload),
                    executionDeadline.Token);
            }
            catch (Exception exception) when (IsTransportFailure(exception))
            {
                // Execute may have reached the Agent. Reconcile only with that Agent; never reroute.
                pendingLease.Dispose();
                await ObserveAndWriteAsync(
                    context,
                    endpoint,
                    resolved,
                    attemptId,
                    deadlineUtc,
                    executionDeadline.Token);
                return;
            }

            if (!started.IsSuccess || started.StatusCode != HttpStatusCode.Accepted)
            {
                if (IsPreStartRejection(started.Error))
                {
                    hadCapacityRejection = true;
                    continue;
                }

                await WriteAgentErrorAsync(context, started.Error, cancellationToken);
                return;
            }
            if (started.Value is null ||
                !string.Equals(started.Value.AttemptId, attemptId, StringComparison.Ordinal))
            {
                await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "agent_attempt_mismatch",
                    "The Function Agent returned a different attempt identity.", cancellationToken);
                return;
            }

            pendingLease.Dispose();
            await ObserveAndWriteAsync(
                context,
                endpoint,
                resolved,
                attemptId,
                deadlineUtc,
                executionDeadline.Token);
            return;
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

    private async Task ObserveAndWriteAsync(
        HttpContext context,
        FunctionEndpoint endpoint,
        ResolvedFunctionHttpRoute resolved,
        string attemptId,
        DateTimeOffset deadlineUtc,
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
                    await WriteCompletionAsync(context, completed, context.RequestAborted);
                    return;
                case ExpiredFunctionAttemptStatus:
                case UnknownFunctionAttemptStatus:
                    await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "attempt_state_unknown",
                        "The Function attempt state is unknown; it was not executed again.", context.RequestAborted);
                    return;
                case ReservedFunctionAttemptStatus:
                case StartedFunctionAttemptStatus:
                    break;
            }
        }

        if (!context.RequestAborted.IsCancellationRequested)
        {
            await WriteErrorAsync(context, StatusCodes.Status504GatewayTimeout, "execution_deadline_exceeded",
                "The Function execution deadline elapsed.", context.RequestAborted);
        }
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
        if (statusCode == StatusCodes.Status429TooManyRequests)
            context.Response.Headers.RetryAfter = "1";
        return context.Response.WriteAsJsonAsync(new FunctionGatewayError(code, message), cancellationToken);
    }
}

public sealed record FunctionGatewayError(string Code, string Message);

internal sealed class FunctionRequestBodyTooLargeException : Exception;
