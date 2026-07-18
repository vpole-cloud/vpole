using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Functions.Protocol;

namespace Pole.Features.Functions.Runtime.Timers;

public interface IFunctionInvocationDispatcher
{
    Task RunOnceAsync(CancellationToken cancellationToken);
}

public sealed class FunctionInvocationDispatcher(
    IFunctionAppDbContext dbContext,
    IFunctionEndpointRegistry endpointRegistry,
    IFunctionEndpointSelector endpointSelector,
    IFunctionRevisionActivator activator,
    IFunctionAgentClient agentClient,
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
        var expired = await dbContext.FunctionInvocations.Find(invocation =>
                (invocation.State == FunctionInvocationState.Dispatching ||
                 invocation.State == FunctionInvocationState.Running) &&
                invocation.Lease != null &&
                invocation.Lease.ExpiresAtUtc <= now)
            .Limit(options.BatchSize)
            .ToListAsync(cancellationToken);
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

    private async Task<FunctionInvocation?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        for (var retry = 0; retry < 5; retry++)
        {
            var now = timeProvider.GetUtcNow();
            var candidate = await dbContext.FunctionInvocations.Find(invocation =>
                    invocation.State == FunctionInvocationState.Pending ||
                    invocation.State == FunctionInvocationState.RetryScheduled &&
                    invocation.NextAttemptAtUtc <= now)
                .SortBy(invocation => invocation.NextAttemptAtUtc)
                .ThenBy(invocation => invocation.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (candidate is null)
                return null;

            var leaseToken = Guid.NewGuid().ToString("N");
            var leased = candidate.AcquireDispatchLease(
                ownerId,
                leaseToken,
                now,
                now.AddSeconds(options.DispatchLeaseSeconds));
            var replaced = await dbContext.FunctionInvocations.ReplaceOneAsync(
                invocation => invocation.Id == candidate.Id &&
                              invocation.State == candidate.State &&
                              invocation.AttemptNumber == candidate.AttemptNumber &&
                              invocation.UpdatedAtUtc == candidate.UpdatedAtUtc &&
                              invocation.Lease == null,
                leased,
                cancellationToken: cancellationToken);
            if (replaced.ModifiedCount == 1)
                return leased;
        }

        return null;
    }

    private async Task DispatchAsync(FunctionInvocation invocation, CancellationToken cancellationToken)
    {
        try
        {
            var app = await dbContext.FunctionApps.Find(candidate =>
                    candidate.TenantId == invocation.TenantId &&
                    candidate.Id == invocation.FunctionAppId &&
                    candidate.DesiredState == FunctionAppDesiredState.Enabled)
                .FirstOrDefaultAsync(cancellationToken);
            var revision = await dbContext.FunctionRevisions.Find(candidate =>
                    candidate.TenantId == invocation.TenantId &&
                    candidate.FunctionAppId == invocation.FunctionAppId &&
                    candidate.Id == invocation.RevisionId &&
                    (candidate.State == FunctionRevisionState.Active ||
                     candidate.State == FunctionRevisionState.Draining))
                .FirstOrDefaultAsync(cancellationToken);
            if (app is null || revision is null)
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

            var subject = new FunctionRevisionInKubernetes(app, revision);
            var service = new FunctionServiceKey(subject.Namespace, subject.ServiceName);
            var snapshot = endpointRegistry.GetSnapshot(service);
            if (!snapshot.HasReadyEndpoints)
            {
                var activation = await activator.EnsureActiveAsync(app, revision, cancellationToken);
                if (!activation.TryGetValue(out snapshot, out var activationFailure))
                {
                    await CompleteFailureAsync(
                        invocation,
                        FunctionInvocationError.Create(
                            "activation_failed",
                            activationFailure.Message,
                            retryable: true),
                        cancellationToken);
                    return;
                }
            }

            var attemptId = CreateAttemptId(invocation.Id, invocation.AttemptNumber);
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
                        cancellationToken);
                }
                catch (Exception exception) when (
                    IsTransportFailure(exception) && !cancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                if (!reservation.IsSuccess || reservation.StatusCode != HttpStatusCode.OK)
                {
                    if (IsCapacityRejection(reservation.Error))
                        continue;
                    await CompleteFailureAsync(
                        invocation,
                        ToInvocationError(reservation.Error, "agent_reservation_failed"),
                        cancellationToken);
                    return;
                }
                if (reservation.Value is null ||
                    !string.Equals(reservation.Value.AttemptId, attemptId, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(reservation.Value.ReservationToken))
                {
                    await CompleteFailureAsync(
                        invocation,
                        FunctionInvocationError.Create(
                            "invalid_agent_response",
                            "The Function Agent returned an invalid reservation.",
                            retryable: true),
                        cancellationToken);
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
                        cancellationToken);
                }
                catch (Exception exception) when (
                    IsTransportFailure(exception) && !cancellationToken.IsCancellationRequested)
                {
                    // Execute may have reached the Agent. The same endpoint is the only safe
                    // reconciliation target; an expired durable lease will create a new attempt.
                }

                if (started is not null && (!started.IsSuccess || started.StatusCode != HttpStatusCode.Accepted))
                {
                    if (IsCapacityRejection(started.Error))
                        continue;
                    await CompleteFailureAsync(
                        invocation,
                        ToInvocationError(started.Error, "agent_execution_failed"),
                        cancellationToken);
                    return;
                }
                if (started is null)
                {
                    await ReconcileAmbiguousExecuteAsync(
                        invocation,
                        endpoint,
                        app,
                        revision,
                        attemptId,
                        deadlineUtc,
                        cancellationToken);
                    return;
                }
                if (started.Value is null ||
                    !string.Equals(started.Value.AttemptId, attemptId, StringComparison.Ordinal))
                {
                    await CompleteFailureAsync(
                        invocation,
                        FunctionInvocationError.Create(
                            "agent_attempt_mismatch",
                            "The Function Agent returned a different attempt identity.",
                            retryable: true),
                        cancellationToken);
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
                await ObserveAsync(running, endpoint, app, revision, attemptId, deadlineUtc, cancellationToken);
                return;
            }

            await CompleteFailureAsync(
                invocation,
                FunctionInvocationError.Create(
                    PoleFunctionProtocolErrors.CapacityExhausted,
                    "No Function Agent instance could accept the Timer invocation.",
                    retryable: true),
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
    }

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
        catch (Exception exception) when (
            IsTransportFailure(exception) && !cancellationToken.IsCancellationRequested)
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
            catch (Exception exception) when (
                IsTransportFailure(exception) && !cancellationToken.IsCancellationRequested)
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
        using var session = await dbContext.Client.StartSessionAsync(cancellationToken: cancellationToken);
        return await session.WithTransactionAsync(async (transaction, transactionCancellationToken) =>
        {
            var replaced = await dbContext.FunctionInvocations.ReplaceOneAsync(
                transaction,
                invocation => invocation.Id == current.Id &&
                              invocation.State == current.State &&
                              invocation.AttemptNumber == current.AttemptNumber &&
                              invocation.Lease != null &&
                              invocation.Lease.Token == current.Lease!.Token,
                next,
                cancellationToken: transactionCancellationToken);
            if (replaced.ModifiedCount != 1)
                return false;
            if (meter is not null)
            {
                await dbContext.FunctionMeterEvents.InsertOneAsync(
                    transaction,
                    meter,
                    cancellationToken: transactionCancellationToken);
            }
            return true;
        }, cancellationToken: cancellationToken);
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
