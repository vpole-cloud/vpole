using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Application.Runtime.Timers;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Coordination;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Functions.Protocol;

namespace Pole.Features.Functions.Runtime.Timers;

public interface IFunctionInvocationDispatcher
{
    Task RunOnceAsync(CancellationToken cancellationToken);
}

public sealed class FunctionInvocationDispatcher(
    LoadExpiredFunctionInvocations loadExpiredInvocations,
    ClaimNextFunctionInvocation claimNextInvocation,
    LoadFunctionInvocationDispatchSubject loadDispatchSubject,
    PersistFunctionInvocationTransition persistTransition,
    IFunctionEndpointRegistry endpointRegistry,
    IFunctionEndpointSelector endpointSelector,
    IFunctionResourcePlacementResolver placementResolver,
    IFunctionRevisionActivator activator,
    IFunctionAgentClient agentClient,
    IFunctionAdmissionCoordinator admissionCoordinator,
    FunctionTimerOptions options,
    TimeProvider timeProvider,
    ILogger<FunctionInvocationDispatcher> logger) : IFunctionInvocationDispatcher
{
    private readonly string ownerId = $"function-dispatcher-{Guid.NewGuid():N}";

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await RecoverExpiredAttemptsAsync(cancellationToken);
        var claimed = new List<FunctionInvocation>();
        for (var index = 0; index < options.BatchSize; index++)
        {
            var invocation = await ClaimNextAsync(cancellationToken);
            if (invocation is null)
                break;
            claimed.Add(invocation);
        }

        await Task.WhenAll(claimed.Select(invocation => DispatchAsync(invocation, cancellationToken)));
    }

    private async Task RecoverExpiredAttemptsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var expired = await loadExpiredInvocations(now, options.BatchSize, cancellationToken);
        foreach (var invocation in expired)
        {
            var error = FunctionInvocationError.Create(
                "attempt_lease_expired",
                "The Function attempt outcome was ambiguous after its dispatch lease expired.",
                retryable: true);
            var recovered = invocation.RecoverExpiredAttempt(error, now, now);
            FunctionMeterEvent? meter = null;
            if (invocation.State == FunctionInvocationState.Running)
            {
                var duration = invocation.AttemptStartedAtUtc is { } startedAtUtc
                    ? Math.Max(0, (long)(now - startedAtUtc).TotalMilliseconds)
                    : 0;
                meter = FunctionMeterEvent.Completed(
                    recovered,
                    recovered.State == FunctionInvocationState.RetryScheduled
                        ? FunctionMeterOutcome.RetryableFailure
                        : FunctionMeterOutcome.PlatformFailure,
                    duration,
                    now);
            }
            await SaveTransitionAsync(invocation, recovered, meter, cancellationToken);
        }
    }

    private Task<FunctionInvocation?> ClaimNextAsync(CancellationToken cancellationToken) =>
        claimNextInvocation(ownerId, options.DispatchLeaseSeconds, cancellationToken);

    private async Task DispatchAsync(FunctionInvocation invocation, CancellationToken cancellationToken)
    {
        FunctionAdmissionLease? admissionLease = null;
        try
        {
            var dispatchSubject = await loadDispatchSubject(invocation, cancellationToken);
            if (dispatchSubject is null)
            {
                await CompleteFailureAsync(
                    invocation,
                    FunctionInvocationError.Create(
                        "revision_unavailable",
                        "The pinned Function revision is no longer available.",
                        retryable: false),
                    cancellationToken);
                return;
            }
            var app = dispatchSubject.App;
            var revision = dispatchSubject.Revision;

            var attemptId = CreateAttemptId(invocation.Id, invocation.AttemptNumber);
            var coordinationKey = new FunctionRevisionCoordinationKey(
                invocation.TenantId,
                invocation.FunctionAppId,
                invocation.RevisionId);
            FunctionAdmissionDecision admission;
            try
            {
                admission = await admissionCoordinator.AcquireAsync(
                    coordinationKey,
                    new FunctionAdmissionRequest(
                        $"timer:{attemptId}",
                        invocation.Id,
                        invocation.HandlerName,
                        FunctionAdmissionTriggerKind.Timer,
                        ReservedBytes: 0),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                await DeferDispatchAsync(
                    invocation,
                    FunctionInvocationError.Create(
                        "function_coordination_unavailable",
                        "Function invocation coordination is temporarily unavailable.",
                        retryable: true),
                    TimeSpan.FromSeconds(RetryDelaySeconds(invocation)),
                    cancellationToken);
                return;
            }

            if (admission.Kind != FunctionAdmissionDecisionKind.Granted ||
                admission.Token is not { } admissionToken)
            {
                var retryDelay = admission.RetryAfterMilliseconds > 0
                    ? TimeSpan.FromMilliseconds(admission.RetryAfterMilliseconds)
                    : TimeSpan.FromSeconds(RetryDelaySeconds(invocation));
                await DeferDispatchAsync(
                    invocation,
                    FunctionInvocationError.Create(
                        admission.Rejection == FunctionAdmissionRejection.RevisionQuiescing
                            ? "function_revision_scaling"
                            : "function_admission_rejected",
                        admission.Rejection == FunctionAdmissionRejection.RevisionQuiescing
                            ? "The Function revision is completing a scaling transition."
                            : "Function invocation admission was temporarily rejected.",
                        retryable: true),
                    retryDelay,
                    cancellationToken);
                return;
            }
            admissionLease = new FunctionAdmissionLease(
                admissionCoordinator,
                coordinationKey,
                admissionToken,
                timeProvider);
            using var admissionOwnership = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                admissionLease.LeaseLostToken);

            FunctionEndpointSnapshot snapshot;
            try
            {
                var renewedForActivation = await RenewDispatchLeaseAsync(
                    invocation,
                    admissionOwnership.Token);
                if (renewedForActivation is null)
                    return;
                invocation = renewedForActivation;

                var resolvedPlacement = await placementResolver.ResolveAsync(
                    app,
                    cancellationToken: admissionOwnership.Token);
                if (resolvedPlacement is FunctionResourcePlacementUnavailable)
                {
                    await DeferDispatchAsync(
                        invocation,
                        FunctionInvocationError.Create(
                            "function_placement_unavailable",
                            "The Function placement is temporarily unavailable.",
                            retryable: true),
                        TimeSpan.FromSeconds(RetryDelaySeconds(invocation)),
                        cancellationToken);
                    return;
                }

                var placement = (FunctionResourcePlacement)resolvedPlacement.Value;
                var subject = new FunctionRevisionInKubernetes(app, revision, placement);
                var service = new FunctionServiceKey(
                    placement.DeploymentTargetId,
                    subject.Namespace,
                    subject.ServiceName);
                snapshot = endpointRegistry.GetSnapshot(service);
                if (!snapshot.HasReadyEndpoints || admission.RequiresActivationFence)
                {
                    var activation = await activator.EnsureActiveAsync(
                        subject,
                        admissionOwnership.Token,
                        forceScaleFence: admission.RequiresActivationFence);
                    if (activation is FunctionRevisionActivationError activationFailure)
                    {
                        await DeferDispatchAsync(
                            invocation,
                            FunctionInvocationError.Create(
                                "activation_failed",
                                activationFailure.Message(),
                                retryable: true),
                            TimeSpan.FromSeconds(RetryDelaySeconds(invocation)),
                            cancellationToken);
                        return;
                    }
                    snapshot = (FunctionEndpointSnapshot)activation.Value;
                }

                var renewedForExecution = await RenewDispatchLeaseAsync(
                    invocation,
                    admissionOwnership.Token);
                if (renewedForExecution is null)
                    return;
                invocation = renewedForExecution;
            }
            catch (OperationCanceledException) when (
                admissionLease.IsLost && !cancellationToken.IsCancellationRequested)
            {
                await DeferDispatchForCoordinationLossAsync(invocation, cancellationToken);
                return;
            }

            using var executionTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(options.ExecutionTimeoutSeconds),
                timeProvider);
            using var executionDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                executionTimeout.Token,
                admissionLease.LeaseLostToken);
            var deadlineUtc = timeProvider.GetUtcNow().AddSeconds(options.ExecutionTimeoutSeconds);
            var identity = new FunctionAttemptIdentity(
                invocation.TenantId,
                invocation.FunctionAppId,
                invocation.RevisionId,
                invocation.HandlerName,
                FunctionTriggerKind.Timer,
                invocation.Id,
                attemptId,
                invocation.AttemptNumber);
            var reserve = new ReserveFunctionAttemptRequest(
                PoleFunctionProtocol.Version,
                identity,
                deadlineUtc,
                new FunctionInvocationTraceContext(null, null, invocation.Id));

            foreach (var endpoint in endpointSelector.Order(snapshot))
            {
                FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse> reservation;
                try
                {
                    reservation = await agentClient.ReserveAsync(
                        endpoint,
                        app,
                        revision,
                        reserve,
                        executionDeadline.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    IsTransportFailure(exception) &&
                    admissionLease.IsLost &&
                    !cancellationToken.IsCancellationRequested)
                {
                    await DeferDispatchForCoordinationLossAsync(invocation, cancellationToken);
                    return;
                }
                catch (Exception exception) when (
                    IsTransportFailure(exception) && executionTimeout.IsCancellationRequested)
                {
                    await DeferDispatchAsync(
                        invocation,
                        FunctionInvocationError.Create(
                            "execution_deadline_exceeded",
                            "The Function Timer attempt deadline elapsed before the Agent accepted it.",
                            retryable: true),
                        TimeSpan.FromSeconds(RetryDelaySeconds(invocation)),
                        cancellationToken);
                    return;
                }
                catch (Exception exception) when (
                    IsTransportFailure(exception) && !executionDeadline.IsCancellationRequested)
                {
                    continue;
                }

                if (!reservation.IsSuccess || reservation.StatusCode != HttpStatusCode.OK)
                {
                    if (IsCapacityRejection(reservation.Error))
                        continue;
                    var error = ToInvocationError(reservation.Error, "agent_reservation_failed");
                    if (error.Retryable)
                    {
                        await DeferDispatchAsync(
                            invocation,
                            error,
                            TimeSpan.FromSeconds(RetryDelaySeconds(invocation)),
                            cancellationToken);
                    }
                    else
                    {
                        await CompleteFailureAsync(invocation, error, cancellationToken);
                    }
                    return;
                }
                if (reservation.Value is null ||
                    !string.Equals(reservation.Value.AttemptId, attemptId, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(reservation.Value.ReservationToken))
                {
                    await DeferDispatchAsync(
                        invocation,
                        FunctionInvocationError.Create(
                            "invalid_agent_response",
                            "The Function Agent returned an invalid reservation.",
                            retryable: true),
                        TimeSpan.FromSeconds(RetryDelaySeconds(invocation)),
                        cancellationToken);
                    return;
                }
                if (admissionLease.IsLost)
                {
                    await DeferDispatchForCoordinationLossAsync(invocation, cancellationToken);
                    return;
                }

                FunctionAgentProtocolResponse<StartFunctionAttemptResponse>? started = null;
                try
                {
                    started = await agentClient.ExecuteAsync(
                        endpoint,
                        app,
                        revision,
                        attemptId,
                        new ExecuteFunctionAttemptRequest(
                            PoleFunctionProtocol.Version,
                            reservation.Value.ReservationToken,
                            new TimerFunctionInvocationPayload(
                                invocation.TriggerId,
                                invocation.ScheduleVersion,
                                invocation.Id,
                                invocation.ScheduledForUtc,
                                timeProvider.GetUtcNow(),
                                invocation.IsPastDue,
                                invocation.MissedOccurrenceCount)),
                        executionDeadline.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    IsTransportFailure(exception) &&
                    (executionTimeout.IsCancellationRequested || admissionLease.IsLost))
                {
                    // Execute may have crossed the start boundary before the deadline or
                    // admission ownership was lost. Recovery must keep the attempt ambiguous.
                }
                catch (Exception exception) when (
                    IsTransportFailure(exception) && !executionDeadline.IsCancellationRequested)
                {
                    // Execute may have reached the Agent. The same endpoint is the only safe
                    // reconciliation target; an expired durable lease will create a new attempt.
                }

                if (started is not null && (!started.IsSuccess || started.StatusCode != HttpStatusCode.Accepted))
                {
                    if (IsCapacityRejection(started.Error))
                        continue;
                    var error = ToInvocationError(started.Error, "agent_execution_failed");
                    if (error.Retryable)
                    {
                        await DeferDispatchAsync(
                            invocation,
                            error,
                            TimeSpan.FromSeconds(RetryDelaySeconds(invocation)),
                            cancellationToken);
                    }
                    else
                    {
                        await CompleteFailureAsync(invocation, error, cancellationToken);
                    }
                    return;
                }
                if (started is null)
                {
                    await ReleaseAdmissionBestEffortAsync(admissionLease);
                    if (executionTimeout.IsCancellationRequested || admissionLease.IsLost)
                        return;
                    await ReconcileAmbiguousExecuteAsync(
                        invocation,
                        endpoint,
                        app,
                        revision,
                        attemptId,
                        deadlineUtc,
                        executionDeadline.Token);
                    return;
                }
                await ReleaseAdmissionBestEffortAsync(admissionLease);
                if (started.Value is null ||
                    !string.Equals(started.Value.AttemptId, attemptId, StringComparison.Ordinal))
                {
                    // HTTP 202 means Execute may have crossed the user-code boundary even if
                    // its response body is malformed. Keep the durable lease ambiguous and
                    // reconcile only with the same Agent; never reroute this attempt.
                    await ReconcileAmbiguousExecuteAsync(
                        invocation,
                        endpoint,
                        app,
                        revision,
                        attemptId,
                        deadlineUtc,
                        executionDeadline.Token);
                    return;
                }

                var running = invocation.MarkRunning(
                    invocation.Lease!.Token,
                    started.Value.StartedAtUtc);
                if (!await SaveTransitionAsync(
                        invocation,
                        running,
                        FunctionMeterEvent.Started(running, running.AttemptStartedAtUtc!.Value),
                        cancellationToken))
                    return;
                await ObserveAsync(
                    running,
                    endpoint,
                    app,
                    revision,
                    attemptId,
                    deadlineUtc,
                    executionDeadline.Token);
                return;
            }

            await DeferDispatchAsync(
                invocation,
                FunctionInvocationError.Create(
                    PoleFunctionProtocolErrors.CapacityExhausted,
                    "No Function Agent instance could accept the Timer invocation.",
                    retryable: true),
                TimeSpan.FromSeconds(RetryDelaySeconds(invocation)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[function-invocation-dispatch-failed] Failed to dispatch invocation {InvocationId}",
                invocation.Id);
        }
        finally
        {
            if (admissionLease is not null)
                await admissionLease.DisposeAsync();
        }
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
            // Admission expiry is the crash/unavailability fallback. Once Execute can have
            // started, Agent status and the durable invocation become the demand sources.
        }
    }

    private async Task<FunctionInvocation?> RenewDispatchLeaseAsync(
        FunctionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var expiresAtUtc = now.AddSeconds(options.DispatchLeaseSeconds);
        if (invocation.Lease is null)
            return null;
        if (invocation.Lease.ExpiresAtUtc >= expiresAtUtc)
            return invocation;

        var renewed = invocation.RenewDispatchLease(
            invocation.Lease.Token,
            now,
            expiresAtUtc);
        return await SaveTransitionAsync(invocation, renewed, meter: null, cancellationToken)
            ? renewed
            : null;
    }

    private async Task DeferDispatchAsync(
        FunctionInvocation invocation,
        FunctionInvocationError error,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var deferred = invocation.DeferDispatch(
            invocation.Lease!.Token,
            now.Add(retryDelay > TimeSpan.Zero ? retryDelay : TimeSpan.FromSeconds(1)),
            error,
            now);
        await SaveTransitionAsync(invocation, deferred, meter: null, cancellationToken);
    }

    private Task DeferDispatchForCoordinationLossAsync(
        FunctionInvocation invocation,
        CancellationToken cancellationToken) =>
        DeferDispatchAsync(
            invocation,
            FunctionInvocationError.Create(
                "function_coordination_lost",
                "Function invocation coordination ownership was lost before the Agent accepted the attempt.",
                retryable: true),
            TimeSpan.FromSeconds(RetryDelaySeconds(invocation)),
            cancellationToken);

    private async Task ReconcileAmbiguousExecuteAsync(
        FunctionInvocation invocation,
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        string attemptId,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        FunctionAgentProtocolResponse<FunctionAttemptStatusResponse> observation;
        try
        {
            observation = await agentClient.ObserveAsync(
                endpoint,
                app,
                revision,
                attemptId,
                waitUpToMilliseconds: 1000,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (
            IsTransportFailure(exception))
        {
            return;
        }

        if (!IsValidObservation(observation, endpoint, attemptId))
            return;
        var startedAtUtc = observation.Value!.Status switch
        {
            StartedFunctionAttemptStatus started => started.StartedAtUtc,
            CompletedFunctionAttemptStatus completed => completed.StartedAtUtc,
            _ => (DateTimeOffset?)null
        };
        if (startedAtUtc is null)
            return;

        var running = invocation.MarkRunning(invocation.Lease!.Token, startedAtUtc.Value);
        if (!await SaveTransitionAsync(
                invocation,
                running,
                FunctionMeterEvent.Started(running, startedAtUtc.Value),
                cancellationToken))
            return;
        if (observation.Value.Status is CompletedFunctionAttemptStatus completedStatus)
        {
            await CompleteObservedAsync(running, completedStatus, cancellationToken);
            return;
        }
        await ObserveAsync(running, endpoint, app, revision, attemptId, deadlineUtc, cancellationToken);
    }

    private async Task ObserveAsync(
        FunctionInvocation running,
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision,
        string attemptId,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken)
    {
        while (timeProvider.GetUtcNow() < deadlineUtc)
        {
            FunctionAgentProtocolResponse<FunctionAttemptStatusResponse> observation;
            try
            {
                observation = await agentClient.ObserveAsync(
                    endpoint,
                    app,
                    revision,
                    attemptId,
                    waitUpToMilliseconds: 1000,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                IsTransportFailure(exception))
            {
                return;
            }

            if (!IsValidObservation(observation, endpoint, attemptId))
                return;
            if (observation.Value!.Status is not CompletedFunctionAttemptStatus completed)
            {
                if (observation.Value.Status is ExpiredFunctionAttemptStatus or UnknownFunctionAttemptStatus)
                    return;
                continue;
            }

            await CompleteObservedAsync(running, completed, cancellationToken);
            return;
        }
    }

    private async Task CompleteObservedAsync(
        FunctionInvocation running,
        CompletedFunctionAttemptStatus completed,
        CancellationToken cancellationToken)
    {
        var duration = Math.Max(0, (long)(completed.CompletedAtUtc - completed.StartedAtUtc).TotalMilliseconds);
        if (completed.Outcome == FunctionAttemptOutcome.Succeeded)
        {
            var succeeded = running.Succeed(running.Lease!.Token, completed.CompletedAtUtc);
            await SaveTransitionAsync(
                running,
                succeeded,
                FunctionMeterEvent.Completed(
                    succeeded,
                    FunctionMeterOutcome.Succeeded,
                    duration,
                    completed.CompletedAtUtc),
                cancellationToken);
            return;
        }

        var failure = FunctionInvocationError.Create(
            completed.Failure?.Code ?? completed.Outcome.ToString(),
            completed.Failure?.Message ?? "The Function Timer attempt failed.",
            completed.Outcome is FunctionAttemptOutcome.RetryableFailure or FunctionAttemptOutcome.TimedOut or
                FunctionAttemptOutcome.PlatformFailure);
        await CompleteFailureAsync(running, failure, cancellationToken, completed.CompletedAtUtc, duration);
    }

    private async Task CompleteFailureAsync(
        FunctionInvocation invocation,
        FunctionInvocationError error,
        CancellationToken cancellationToken,
        DateTimeOffset? occurredAtUtc = null,
        long executionDurationMilliseconds = 0)
    {
        var now = occurredAtUtc ?? timeProvider.GetUtcNow();
        FunctionInvocation completed;
        FunctionMeterOutcome outcome;
        if (error.Retryable && invocation.CanRetry)
        {
            completed = invocation.ScheduleRetry(
                invocation.Lease!.Token,
                now.AddSeconds(RetryDelaySeconds(invocation)),
                error,
                now);
            outcome = FunctionMeterOutcome.RetryableFailure;
        }
        else
        {
            completed = invocation.Fail(invocation.Lease!.Token, error, now);
            outcome = error.Retryable
                ? FunctionMeterOutcome.PlatformFailure
                : FunctionMeterOutcome.PermanentFailure;
        }

        var meter = invocation.State == FunctionInvocationState.Running
            ? FunctionMeterEvent.Completed(completed, outcome, executionDurationMilliseconds, now)
            : null;
        await SaveTransitionAsync(invocation, completed, meter, cancellationToken);
    }

    private async Task<bool> SaveTransitionAsync(
        FunctionInvocation current,
        FunctionInvocation next,
        FunctionMeterEvent? meter,
        CancellationToken cancellationToken)
    {
        return await persistTransition(current, next, meter, cancellationToken);
    }

    private static int RetryDelaySeconds(FunctionInvocation invocation)
    {
        if (invocation.Retry.BackoffSeconds.Count == 0)
            return 1;
        var index = Math.Min(invocation.AttemptNumber - 1, invocation.Retry.BackoffSeconds.Count - 1);
        return Math.Max(1, invocation.Retry.BackoffSeconds[index]);
    }

    private static FunctionInvocationError ToInvocationError(FunctionProtocolError? error, string fallbackCode) =>
        FunctionInvocationError.Create(
            error?.Code ?? fallbackCode,
            error?.Message ?? "The Function Agent rejected the Timer invocation.",
            error?.Retryable ?? true);

    private static bool IsCapacityRejection(FunctionProtocolError? error) =>
        error?.Code is PoleFunctionProtocolErrors.CapacityExhausted or PoleFunctionProtocolErrors.AgentDraining;

    private static bool IsTransportFailure(Exception exception) =>
        exception is HttpRequestException or FunctionAgentProtocolException or OperationCanceledException;

    private static bool IsValidObservation(
        FunctionAgentProtocolResponse<FunctionAttemptStatusResponse> observation,
        FunctionEndpoint endpoint,
        string attemptId) =>
        observation.IsSuccess &&
        observation.StatusCode == HttpStatusCode.OK &&
        observation.Value?.Status is not null &&
        string.Equals(observation.Value.AgentInstanceId, endpoint.TargetUid, StringComparison.Ordinal) &&
        string.Equals(observation.Value.Status.AttemptId, attemptId, StringComparison.Ordinal);

    private static string CreateAttemptId(string invocationId, int attemptNumber) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{invocationId}:{attemptNumber}")))
            .ToLowerInvariant();
}

public sealed class FunctionInvocationDispatcherWorker(
    IFunctionInvocationDispatcher dispatcher,
    FunctionTimerOptions options,
    TimeProvider timeProvider,
    ILogger<FunctionInvocationDispatcherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SweepIntervalSeconds), timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await dispatcher.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[function-invocation-dispatch-cycle-failed] Dispatch cycle failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
                break;
        }
    }
}
