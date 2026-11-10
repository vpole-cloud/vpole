using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.Functions.Domain;

public sealed record FunctionExecutionLease(
    string OwnerId,
    string Token,
    DateTimeOffset AcquiredAtUtc,
    DateTimeOffset ExpiresAtUtc
)
{
    public static FunctionExecutionLease Create(
        string ownerId,
        string token,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        FunctionDurableModelGuard.NotBlank(ownerId, nameof(ownerId));
        FunctionDurableModelGuard.NotBlank(token, nameof(token));
        FunctionDurableModelGuard.Utc(acquiredAtUtc, nameof(acquiredAtUtc));
        FunctionDurableModelGuard.Utc(expiresAtUtc, nameof(expiresAtUtc));
        if (expiresAtUtc <= acquiredAtUtc)
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "A lease must expire after it is acquired.");

        return new FunctionExecutionLease(ownerId, token, acquiredAtUtc, expiresAtUtc);
    }

    public bool IsActiveAt(DateTimeOffset instantUtc)
    {
        FunctionDurableModelGuard.Utc(instantUtc, nameof(instantUtc));
        return ExpiresAtUtc > instantUtc;
    }
}

public sealed record FunctionTimerState(
    [property: BsonId] string Id,
    string TenantId,
    string FunctionAppId,
    string RevisionId,
    string TriggerId,
    string HandlerName,
    string Cron,
    string TimeZone,
    int ScheduleVersion,
    TimerConcurrencyPolicy ConcurrencyPolicy,
    TimerMisfirePolicy MisfirePolicy,
    FunctionRetryPolicy Retry,
    DateTimeOffset NextOccurrenceUtc,
    bool Enabled,
    FunctionExecutionLease? Lease,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc
) : ITenantOwned
{
    public const string UtcTimeZone = "UTC";

    public static FunctionTimerState Create(
        string tenantId,
        string functionAppId,
        string revisionId,
        string triggerId,
        string handlerName,
        string cron,
        int scheduleVersion,
        TimerConcurrencyPolicy concurrencyPolicy,
        TimerMisfirePolicy misfirePolicy,
        FunctionRetryPolicy retry,
        DateTimeOffset nextOccurrenceUtc,
        DateTimeOffset nowUtc) => new(
        FunctionDurableIdentity.Create("timer", tenantId, functionAppId, triggerId),
        FunctionDurableModelGuard.NotBlank(tenantId, nameof(tenantId)),
        FunctionDurableModelGuard.NotBlank(functionAppId, nameof(functionAppId)),
        FunctionDurableModelGuard.NotBlank(revisionId, nameof(revisionId)),
        FunctionDurableModelGuard.NotBlank(triggerId, nameof(triggerId)),
        FunctionDurableModelGuard.NotBlank(handlerName, nameof(handlerName)),
        FunctionDurableModelGuard.NotBlank(cron, nameof(cron)),
        UtcTimeZone,
        FunctionDurableModelGuard.Positive(scheduleVersion, nameof(scheduleVersion)),
        concurrencyPolicy,
        misfirePolicy,
        FunctionDurableModelGuard.Retry(retry),
        FunctionDurableModelGuard.Utc(nextOccurrenceUtc, nameof(nextOccurrenceUtc)),
        Enabled: true,
        Lease: null,
        FunctionDurableModelGuard.Utc(nowUtc, nameof(nowUtc)),
        nowUtc);

    public FunctionTimerState Reconcile(
        string revisionId,
        string handlerName,
        string cron,
        int scheduleVersion,
        TimerConcurrencyPolicy concurrencyPolicy,
        TimerMisfirePolicy misfirePolicy,
        FunctionRetryPolicy retry,
        DateTimeOffset nextOccurrenceUtc,
        DateTimeOffset nowUtc)
    {
        FunctionDurableModelGuard.NotBlank(revisionId, nameof(revisionId));
        FunctionDurableModelGuard.NotBlank(handlerName, nameof(handlerName));
        FunctionDurableModelGuard.NotBlank(cron, nameof(cron));
        FunctionDurableModelGuard.Positive(scheduleVersion, nameof(scheduleVersion));
        FunctionDurableModelGuard.Retry(retry);
        FunctionDurableModelGuard.Utc(nextOccurrenceUtc, nameof(nextOccurrenceUtc));
        FunctionDurableModelGuard.Utc(nowUtc, nameof(nowUtc));
        if (scheduleVersion < ScheduleVersion)
            throw new InvalidOperationException("A timer schedule cannot move to an older schedule version.");

        if (scheduleVersion == ScheduleVersion)
        {
            var unchanged = string.Equals(HandlerName, handlerName, StringComparison.Ordinal) &&
                string.Equals(Cron, cron, StringComparison.Ordinal) && ConcurrencyPolicy == concurrencyPolicy &&
                MisfirePolicy == misfirePolicy && NextOccurrenceUtc == nextOccurrenceUtc;
            if (!unchanged)
                throw new InvalidOperationException(
                    "A schedule version cannot describe different timer configuration.");

            return this with
            {
                RevisionId = revisionId,
                Retry = retry,
                Enabled = true,
                Lease = null,
                UpdatedAtUtc = nowUtc,
            };
        }

        return this with
        {
            RevisionId = revisionId,
            HandlerName = handlerName,
            Cron = cron,
            ScheduleVersion = scheduleVersion,
            ConcurrencyPolicy = concurrencyPolicy,
            MisfirePolicy = misfirePolicy,
            Retry = retry,
            NextOccurrenceUtc = nextOccurrenceUtc,
            Enabled = true,
            Lease = null,
            UpdatedAtUtc = nowUtc,
        };
    }

    public FunctionTimerState AcquireLease(
        string ownerId,
        string token,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (!Enabled)
            throw new InvalidOperationException("A disabled timer schedule cannot be leased.");
        if (Lease is { } current && current.IsActiveAt(acquiredAtUtc) &&
            !string.Equals(current.Token, token, StringComparison.Ordinal))
            throw new InvalidOperationException("The timer schedule already has an active lease.");

        return this with
        {
            Lease = FunctionExecutionLease.Create(ownerId, token, acquiredAtUtc, expiresAtUtc),
            UpdatedAtUtc = acquiredAtUtc,
        };
    }

    public FunctionTimerState Advance(
        string leaseToken,
        DateTimeOffset nextOccurrenceUtc,
        DateTimeOffset nowUtc)
    {
        RequireLease(leaseToken, nowUtc);
        FunctionDurableModelGuard.Utc(nextOccurrenceUtc, nameof(nextOccurrenceUtc));
        if (nextOccurrenceUtc <= NextOccurrenceUtc)
            throw new ArgumentOutOfRangeException(
                nameof(nextOccurrenceUtc),
                "The next occurrence must advance the schedule.");

        return this with
        {
            NextOccurrenceUtc = nextOccurrenceUtc,
            Lease = null,
            UpdatedAtUtc = nowUtc,
        };
    }

    public FunctionTimerState ReleaseLease(string leaseToken, DateTimeOffset nowUtc)
    {
        RequireLeaseToken(leaseToken);
        FunctionDurableModelGuard.Utc(nowUtc, nameof(nowUtc));
        return this with { Lease = null, UpdatedAtUtc = nowUtc };
    }

    public FunctionTimerState Disable(DateTimeOffset nowUtc)
    {
        FunctionDurableModelGuard.Utc(nowUtc, nameof(nowUtc));
        return this with { Enabled = false, Lease = null, UpdatedAtUtc = nowUtc };
    }

    public bool HasValidState() => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(TenantId) &&
        !string.IsNullOrWhiteSpace(FunctionAppId) && !string.IsNullOrWhiteSpace(RevisionId) &&
        !string.IsNullOrWhiteSpace(TriggerId) && !string.IsNullOrWhiteSpace(HandlerName) &&
        !string.IsNullOrWhiteSpace(Cron) && TimeZone == UtcTimeZone && ScheduleVersion > 0 &&
        NextOccurrenceUtc.Offset == TimeSpan.Zero && CreatedAtUtc.Offset == TimeSpan.Zero &&
        UpdatedAtUtc.Offset == TimeSpan.Zero && (Enabled || Lease is null) &&
        (Lease is null || Lease.ExpiresAtUtc > Lease.AcquiredAtUtc);

    private void RequireLease(string leaseToken, DateTimeOffset nowUtc)
    {
        FunctionDurableModelGuard.Utc(nowUtc, nameof(nowUtc));
        RequireLeaseToken(leaseToken);
        if (!Lease!.IsActiveAt(nowUtc))
            throw new InvalidOperationException("The timer schedule lease has expired.");
    }

    private void RequireLeaseToken(string leaseToken)
    {
        FunctionDurableModelGuard.NotBlank(leaseToken, nameof(leaseToken));
        if (Lease is null || !string.Equals(Lease.Token, leaseToken, StringComparison.Ordinal))
            throw new InvalidOperationException("The timer schedule is not owned by this lease.");
    }
}

public enum FunctionInvocationState
{
    Pending,
    Dispatching,
    Running,
    RetryScheduled,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
}

public static class FunctionInvocationStateExtensions
{
    public static bool IsTerminal(this FunctionInvocationState state) => state is FunctionInvocationState.Succeeded
        or FunctionInvocationState.Failed or FunctionInvocationState.Skipped or FunctionInvocationState.Cancelled;

    public static bool IsNonTerminal(this FunctionInvocationState state) => !state.IsTerminal();
}

public sealed record FunctionInvocationError(
    string Code,
    string Message,
    bool Retryable
)
{
    public static FunctionInvocationError Create(
        string code,
        string message,
        bool retryable) => new(
        FunctionDurableModelGuard.NotBlank(code, nameof(code)),
        FunctionDurableModelGuard.NotBlank(message, nameof(message)),
        retryable);
}

public sealed record FunctionInvocation(
    [property: BsonId] string Id,
    string TenantId,
    string FunctionAppId,
    string RevisionId,
    string TriggerId,
    string HandlerName,
    int ScheduleVersion,
    DateTimeOffset ScheduledForUtc,
    bool IsPastDue,
    int MissedOccurrenceCount,
    FunctionRetryPolicy Retry,
    FunctionInvocationState State,
    int AttemptNumber,
    FunctionExecutionLease? Lease,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? AttemptStartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    FunctionInvocationError? Error
) : ITenantOwned
{
    public bool CanRetry =>
        State is FunctionInvocationState.Dispatching or FunctionInvocationState.Running &&
        AttemptNumber < Retry.MaxAttempts;

    public static FunctionInvocation CreateTimer(
        string tenantId,
        string functionAppId,
        string revisionId,
        string triggerId,
        string handlerName,
        int scheduleVersion,
        DateTimeOffset scheduledForUtc,
        FunctionRetryPolicy retry,
        DateTimeOffset nowUtc,
        bool isPastDue = false,
        int missedOccurrenceCount = 0) => new(
        FunctionDurableIdentity.Create(
            "invocation",
            tenantId,
            triggerId,
            scheduleVersion.ToString(CultureInfo.InvariantCulture),
            scheduledForUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        FunctionDurableModelGuard.NotBlank(tenantId, nameof(tenantId)),
        FunctionDurableModelGuard.NotBlank(functionAppId, nameof(functionAppId)),
        FunctionDurableModelGuard.NotBlank(revisionId, nameof(revisionId)),
        FunctionDurableModelGuard.NotBlank(triggerId, nameof(triggerId)),
        FunctionDurableModelGuard.NotBlank(handlerName, nameof(handlerName)),
        FunctionDurableModelGuard.Positive(scheduleVersion, nameof(scheduleVersion)),
        FunctionDurableModelGuard.Utc(scheduledForUtc, nameof(scheduledForUtc)),
        isPastDue,
        FunctionDurableModelGuard.NonNegative(missedOccurrenceCount, nameof(missedOccurrenceCount)),
        FunctionDurableModelGuard.Retry(retry),
        FunctionInvocationState.Pending,
        AttemptNumber: 1,
        Lease: null,
        FunctionDurableModelGuard.Utc(nowUtc, nameof(nowUtc)),
        nowUtc,
        AttemptStartedAtUtc: null,
        CompletedAtUtc: null,
        NextAttemptAtUtc: null,
        Error: null);

    public FunctionInvocation AcquireDispatchLease(
        string ownerId,
        string token,
        DateTimeOffset acquiredAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (State.IsTerminal())
            throw new InvalidOperationException($"A terminal invocation in state '{State}' cannot be dispatched.");
        if ((State == FunctionInvocationState.Pending && NextAttemptAtUtc is { } pendingDispatchAtUtc &&
                pendingDispatchAtUtc > acquiredAtUtc) || (State == FunctionInvocationState.RetryScheduled &&
                (NextAttemptAtUtc is null || NextAttemptAtUtc > acquiredAtUtc)))
            throw new InvalidOperationException("The invocation dispatch is not due yet.");
        if (State is FunctionInvocationState.Dispatching or FunctionInvocationState.Running && Lease is { } current &&
            current.IsActiveAt(acquiredAtUtc) && !string.Equals(current.Token, token, StringComparison.Ordinal))
            throw new InvalidOperationException("The invocation already has an active dispatch lease.");

        var nextAttemptNumber = State == FunctionInvocationState.RetryScheduled
            ? checked(AttemptNumber + 1)
            : AttemptNumber;

        return this with
        {
            State = FunctionInvocationState.Dispatching,
            AttemptNumber = nextAttemptNumber,
            Lease = FunctionExecutionLease.Create(ownerId, token, acquiredAtUtc, expiresAtUtc),
            UpdatedAtUtc = acquiredAtUtc,
            AttemptStartedAtUtc = null,
            NextAttemptAtUtc = null,
        };
    }

    public FunctionInvocation RenewDispatchLease(
        string leaseToken,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        RequireDispatchingOrRunning();
        RequireActiveLease(leaseToken, renewedAtUtc);
        FunctionDurableModelGuard.Utc(expiresAtUtc, nameof(expiresAtUtc));
        if (expiresAtUtc <= renewedAtUtc)
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "A renewed lease must expire in the future.");

        return this with
        {
            Lease = Lease! with { ExpiresAtUtc = expiresAtUtc },
            UpdatedAtUtc = renewedAtUtc,
        };
    }

    public FunctionInvocation DeferDispatch(
        string leaseToken,
        DateTimeOffset nextDispatchAtUtc,
        FunctionInvocationError reason,
        DateTimeOffset nowUtc)
    {
        RequireState(FunctionInvocationState.Dispatching);
        RequireActiveLease(leaseToken, nowUtc);
        FunctionDurableModelGuard.Utc(nextDispatchAtUtc, nameof(nextDispatchAtUtc));
        ArgumentNullException.ThrowIfNull(reason);
        if (nextDispatchAtUtc <= nowUtc)
            throw new ArgumentOutOfRangeException(
                nameof(nextDispatchAtUtc),
                "A deferred dispatch must be scheduled in the future.");
        if (!reason.Retryable)
            throw new InvalidOperationException("A deferred dispatch requires a retryable platform error.");

        return this with
        {
            State = FunctionInvocationState.Pending,
            Lease = null,
            UpdatedAtUtc = nowUtc,
            AttemptStartedAtUtc = null,
            NextAttemptAtUtc = nextDispatchAtUtc,
            Error = reason,
        };
    }

    public FunctionInvocation MarkRunning(string leaseToken, DateTimeOffset startedAtUtc)
    {
        RequireState(FunctionInvocationState.Dispatching);
        RequireActiveLease(leaseToken, startedAtUtc);
        return this with
        {
            State = FunctionInvocationState.Running,
            UpdatedAtUtc = startedAtUtc,
            AttemptStartedAtUtc = startedAtUtc,
            Error = null,
        };
    }

    public FunctionInvocation ScheduleRetry(
        string leaseToken,
        DateTimeOffset nextAttemptAtUtc,
        FunctionInvocationError error,
        DateTimeOffset nowUtc)
    {
        RequireDispatchingOrRunning();
        RequireActiveLease(leaseToken, nowUtc);
        FunctionDurableModelGuard.Utc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        if (nextAttemptAtUtc <= nowUtc)
            throw new ArgumentOutOfRangeException(nameof(nextAttemptAtUtc), "A retry must be scheduled in the future.");
        if (!error.Retryable)
            throw new InvalidOperationException("A retry requires a retryable invocation error.");
        if (AttemptNumber >= Retry.MaxAttempts)
            throw new InvalidOperationException("The invocation has exhausted its retry attempts.");

        return this with
        {
            State = FunctionInvocationState.RetryScheduled,
            Lease = null,
            UpdatedAtUtc = nowUtc,
            AttemptStartedAtUtc = null,
            NextAttemptAtUtc = nextAttemptAtUtc,
            Error = error,
        };
    }

    public FunctionInvocation Succeed(string leaseToken, DateTimeOffset completedAtUtc)
    {
        RequireState(FunctionInvocationState.Running);
        RequireActiveLease(leaseToken, completedAtUtc);
        return Complete(FunctionInvocationState.Succeeded, completedAtUtc, error: null);
    }

    public FunctionInvocation Fail(
        string leaseToken,
        FunctionInvocationError error,
        DateTimeOffset completedAtUtc)
    {
        RequireDispatchingOrRunning();
        RequireActiveLease(leaseToken, completedAtUtc);
        return Complete(FunctionInvocationState.Failed, completedAtUtc, error);
    }

    public FunctionInvocation RecoverExpiredAttempt(
        FunctionInvocationError error,
        DateTimeOffset nowUtc,
        DateTimeOffset nextAttemptAtUtc)
    {
        RequireDispatchingOrRunning();
        FunctionDurableModelGuard.Utc(nowUtc, nameof(nowUtc));
        FunctionDurableModelGuard.Utc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        if (Lease is null || Lease.IsActiveAt(nowUtc))
            throw new InvalidOperationException("Only an invocation with an expired lease can be recovered.");
        if (!error.Retryable)
            throw new InvalidOperationException("Expired attempt recovery requires a retryable error.");

        return AttemptNumber < Retry.MaxAttempts
            ? this with
            {
                State = FunctionInvocationState.RetryScheduled,
                Lease = null,
                UpdatedAtUtc = nowUtc,
                AttemptStartedAtUtc = null,
                NextAttemptAtUtc = nextAttemptAtUtc,
                Error = error,
            }
            : Complete(FunctionInvocationState.Failed, nowUtc, error);
    }

    public FunctionInvocation Skip(FunctionInvocationError reason, DateTimeOffset completedAtUtc)
    {
        if (State is not (FunctionInvocationState.Pending or FunctionInvocationState.RetryScheduled))
            throw new InvalidOperationException($"Cannot skip an invocation in state '{State}'.");
        return Complete(FunctionInvocationState.Skipped, completedAtUtc, reason);
    }

    public FunctionInvocation Cancel(FunctionInvocationError reason, DateTimeOffset completedAtUtc)
    {
        if (State.IsTerminal())
            throw new InvalidOperationException($"Cannot cancel an invocation in terminal state '{State}'.");
        return Complete(FunctionInvocationState.Cancelled, completedAtUtc, reason);
    }

    public FunctionInvocation RetryManually(DateTimeOffset nowUtc)
    {
        FunctionDurableModelGuard.Utc(nowUtc, nameof(nowUtc));
        if (State != FunctionInvocationState.Failed)
            throw new InvalidOperationException("Only a failed invocation can be retried manually.");
        return this with
        {
            State = FunctionInvocationState.RetryScheduled,
            Lease = null,
            UpdatedAtUtc = nowUtc,
            AttemptStartedAtUtc = null,
            CompletedAtUtc = null,
            NextAttemptAtUtc = nowUtc,
            Error = FunctionInvocationError.Create(
                "manual_retry",
                "The invocation was queued for a manual retry.",
                retryable: true),
        };
    }

    public bool HasValidState()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(TenantId) ||
            string.IsNullOrWhiteSpace(FunctionAppId) || string.IsNullOrWhiteSpace(RevisionId) ||
            string.IsNullOrWhiteSpace(TriggerId) || string.IsNullOrWhiteSpace(HandlerName) || ScheduleVersion <= 0 ||
            AttemptNumber <= 0 || MissedOccurrenceCount < 0 || ScheduledForUtc.Offset != TimeSpan.Zero ||
            CreatedAtUtc.Offset != TimeSpan.Zero || UpdatedAtUtc.Offset != TimeSpan.Zero)
            return false;

        return State switch
        {
            FunctionInvocationState.Pending => Lease is null && AttemptStartedAtUtc is null && CompletedAtUtc is null &&
                ((NextAttemptAtUtc is null && Error is null) ||
                    (NextAttemptAtUtc is not null && Error is { Retryable: true })),
            FunctionInvocationState.Dispatching => Lease is not null && AttemptStartedAtUtc is null &&
                CompletedAtUtc is null && NextAttemptAtUtc is null,
            FunctionInvocationState.Running => Lease is not null && AttemptStartedAtUtc is not null &&
                CompletedAtUtc is null && NextAttemptAtUtc is null && Error is null,
            FunctionInvocationState.RetryScheduled => Lease is null && AttemptStartedAtUtc is null &&
                CompletedAtUtc is null && NextAttemptAtUtc is not null && Error is { Retryable: true },
            FunctionInvocationState.Succeeded => Lease is null && CompletedAtUtc is not null &&
                NextAttemptAtUtc is null && Error is null,
            FunctionInvocationState.Failed or FunctionInvocationState.Skipped or FunctionInvocationState.Cancelled =>
                Lease is null && CompletedAtUtc is not null && NextAttemptAtUtc is null && Error is not null,
            _ => false,
        };
    }

    private FunctionInvocation Complete(
        FunctionInvocationState state,
        DateTimeOffset completedAtUtc,
        FunctionInvocationError? error)
    {
        FunctionDurableModelGuard.Utc(completedAtUtc, nameof(completedAtUtc));
        return this with
        {
            State = state,
            Lease = null,
            UpdatedAtUtc = completedAtUtc,
            CompletedAtUtc = completedAtUtc,
            NextAttemptAtUtc = null,
            Error = error,
        };
    }

    private void RequireDispatchingOrRunning()
    {
        if (State is not (FunctionInvocationState.Dispatching or FunctionInvocationState.Running))
            throw new InvalidOperationException($"Invocation state '{State}' has no active attempt.");
    }

    private void RequireState(FunctionInvocationState expected)
    {
        if (State != expected)
            throw new InvalidOperationException($"Expected invocation state '{expected}', but found '{State}'.");
    }

    private void RequireActiveLease(string leaseToken, DateTimeOffset nowUtc)
    {
        FunctionDurableModelGuard.NotBlank(leaseToken, nameof(leaseToken));
        FunctionDurableModelGuard.Utc(nowUtc, nameof(nowUtc));
        if (Lease is null || !string.Equals(Lease.Token, leaseToken, StringComparison.Ordinal))
            throw new InvalidOperationException("The invocation is not owned by this lease.");
        if (!Lease.IsActiveAt(nowUtc))
            throw new InvalidOperationException("The invocation lease has expired.");
    }
}

public enum FunctionMeterTriggerKind
{
    Http,
    Timer,
}

public enum FunctionMeterEventPhase
{
    Admitted,
    Started,
    Completed,
}

public enum FunctionMeterOutcome
{
    Succeeded,
    RetryableFailure,
    PermanentFailure,
    TimedOut,
    Cancelled,
    PlatformFailure,
    Skipped,
}

public sealed record FunctionMeterDimensions(
    string TenantId,
    string FunctionAppId,
    string RevisionId,
    string TriggerId,
    string HandlerName,
    FunctionMeterTriggerKind TriggerKind
);

public sealed record FunctionMeterEvent(
    [property: BsonId] string Id,
    string TenantId,
    string FunctionAppId,
    string RevisionId,
    string TriggerId,
    string HandlerName,
    FunctionMeterTriggerKind TriggerKind,
    string InvocationId,
    int AttemptNumber,
    FunctionMeterEventPhase Phase,
    FunctionMeterOutcome? Outcome,
    long? ExecutionDurationMilliseconds,
    DateTimeOffset OccurredAtUtc,
    DateTime OccurredAtUtcDateTime,
    DateTimeOffset? AggregatedAtUtc
) : ITenantOwned
{
    public FunctionMeterDimensions Dimensions =>
        new(TenantId, FunctionAppId, RevisionId, TriggerId, HandlerName, TriggerKind);

    public static FunctionMeterEvent Admitted(FunctionInvocation invocation, DateTimeOffset occurredAtUtc) => Create(
        invocation,
        FunctionMeterEventPhase.Admitted,
        outcome: null,
        executionDurationMilliseconds: null,
        occurredAtUtc);

    public static FunctionMeterEvent CreateFact(
        FunctionMeterDimensions dimensions,
        string invocationId,
        int attemptNumber,
        FunctionMeterEventPhase phase,
        FunctionMeterOutcome? outcome,
        long? executionDurationMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        FunctionDurableModelGuard.Dimensions(dimensions);
        FunctionDurableModelGuard.NotBlank(invocationId, nameof(invocationId));
        FunctionDurableModelGuard.Positive(attemptNumber, nameof(attemptNumber));
        FunctionDurableModelGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
        if (phase == FunctionMeterEventPhase.Completed &&
            (outcome is null || executionDurationMilliseconds is null or < 0))
            throw new InvalidOperationException("A completed meter fact requires an outcome and duration.");
        if (phase != FunctionMeterEventPhase.Completed &&
            (outcome is not null || executionDurationMilliseconds is not null))
            throw new InvalidOperationException("Only a completed meter fact can contain an outcome and duration.");

        return new FunctionMeterEvent(
            FunctionDurableIdentity.Create(
                "meter",
                invocationId,
                attemptNumber.ToString(CultureInfo.InvariantCulture),
                phase.ToString()),
            dimensions.TenantId,
            dimensions.FunctionAppId,
            dimensions.RevisionId,
            dimensions.TriggerId,
            dimensions.HandlerName,
            dimensions.TriggerKind,
            invocationId,
            attemptNumber,
            phase,
            outcome,
            executionDurationMilliseconds,
            occurredAtUtc,
            occurredAtUtc.UtcDateTime,
            AggregatedAtUtc: null);
    }

    public static FunctionMeterEvent Started(FunctionInvocation invocation, DateTimeOffset occurredAtUtc)
    {
        if (invocation.State != FunctionInvocationState.Running)
            throw new InvalidOperationException("A started meter event requires a running invocation.");

        return Create(
            invocation,
            FunctionMeterEventPhase.Started,
            outcome: null,
            executionDurationMilliseconds: null,
            occurredAtUtc);
    }

    public static FunctionMeterEvent Completed(
        FunctionInvocation invocation,
        FunctionMeterOutcome outcome,
        long executionDurationMilliseconds,
        DateTimeOffset occurredAtUtc)
    {
        if (invocation.State.IsNonTerminal() && invocation.State != FunctionInvocationState.RetryScheduled)
            throw new InvalidOperationException("A completed meter event requires a finished attempt.");
        if (executionDurationMilliseconds < 0)
            throw new ArgumentOutOfRangeException(
                nameof(executionDurationMilliseconds),
                "Execution duration cannot be negative.");

        return Create(
            invocation,
            FunctionMeterEventPhase.Completed,
            outcome,
            executionDurationMilliseconds,
            occurredAtUtc);
    }

    private static FunctionMeterEvent Create(
        FunctionInvocation invocation,
        FunctionMeterEventPhase phase,
        FunctionMeterOutcome? outcome,
        long? executionDurationMilliseconds,
        DateTimeOffset occurredAtUtc) => CreateFact(
        new FunctionMeterDimensions(
            invocation.TenantId,
            invocation.FunctionAppId,
            invocation.RevisionId,
            invocation.TriggerId,
            invocation.HandlerName,
            FunctionMeterTriggerKind.Timer),
        invocation.Id,
        invocation.AttemptNumber,
        phase,
        outcome,
        executionDurationMilliseconds,
        occurredAtUtc);
}

public sealed record FunctionUsageBucket(
    [property: BsonId] string Id,
    string TenantId,
    string FunctionAppId,
    string RevisionId,
    string TriggerId,
    string HandlerName,
    FunctionMeterTriggerKind TriggerKind,
    DateTimeOffset HourUtc,
    long InvocationCount,
    long AttemptCount,
    long SucceededAttemptCount,
    long FailedAttemptCount,
    long SkippedCount,
    long ExecutionDurationMilliseconds,
    DateTimeOffset UpdatedAtUtc
) : ITenantOwned
{
    public static FunctionUsageBucket Create(FunctionMeterDimensions dimensions, DateTimeOffset hourUtc)
    {
        FunctionDurableModelGuard.Dimensions(dimensions);
        FunctionDurableModelGuard.Utc(hourUtc, nameof(hourUtc));
        if (hourUtc.Minute != 0 || hourUtc.Second != 0 || hourUtc.Millisecond != 0 ||
            hourUtc.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(hourUtc), "A usage bucket must start on an exact UTC hour.");

        return new FunctionUsageBucket(
            FunctionDurableIdentity.Create(
                "usage-hour",
                dimensions.TenantId,
                dimensions.FunctionAppId,
                dimensions.RevisionId,
                dimensions.TriggerId,
                dimensions.HandlerName,
                dimensions.TriggerKind.ToString(),
                hourUtc.ToString("O", CultureInfo.InvariantCulture)),
            dimensions.TenantId,
            dimensions.FunctionAppId,
            dimensions.RevisionId,
            dimensions.TriggerId,
            dimensions.HandlerName,
            dimensions.TriggerKind,
            hourUtc,
            InvocationCount: 0,
            AttemptCount: 0,
            SucceededAttemptCount: 0,
            FailedAttemptCount: 0,
            SkippedCount: 0,
            ExecutionDurationMilliseconds: 0,
            UpdatedAtUtc: hourUtc);
    }

    public FunctionUsageBucket Apply(FunctionMeterEvent meterEvent)
    {
        if (meterEvent.Dimensions != new FunctionMeterDimensions(
                TenantId,
                FunctionAppId,
                RevisionId,
                TriggerId,
                HandlerName,
                TriggerKind))
            throw new InvalidOperationException("The meter event dimensions do not match this usage bucket.");
        if (ToHour(meterEvent.OccurredAtUtc) != HourUtc)
            throw new InvalidOperationException("The meter event belongs to a different usage hour.");

        return meterEvent.Phase switch
        {
            FunctionMeterEventPhase.Admitted => this with
            {
                InvocationCount = checked(InvocationCount + 1),
                UpdatedAtUtc = meterEvent.OccurredAtUtc,
            },
            FunctionMeterEventPhase.Started => this with
            {
                AttemptCount = checked(AttemptCount + 1),
                UpdatedAtUtc = meterEvent.OccurredAtUtc,
            },
            FunctionMeterEventPhase.Completed => ApplyCompleted(meterEvent),
            _ => throw new InvalidOperationException($"Unknown meter event phase '{meterEvent.Phase}'."),
        };
    }

    public static DateTimeOffset ToHour(DateTimeOffset instantUtc)
    {
        FunctionDurableModelGuard.Utc(instantUtc, nameof(instantUtc));
        return new DateTimeOffset(
            instantUtc.Year,
            instantUtc.Month,
            instantUtc.Day,
            instantUtc.Hour,
            0,
            0,
            TimeSpan.Zero);
    }

    private FunctionUsageBucket ApplyCompleted(FunctionMeterEvent meterEvent)
    {
        if (meterEvent.Outcome is null || meterEvent.ExecutionDurationMilliseconds is null)
            throw new InvalidOperationException("A completed meter event requires an outcome and execution duration.");

        var succeeded = meterEvent.Outcome == FunctionMeterOutcome.Succeeded ? 1L : 0L;
        var skipped = meterEvent.Outcome == FunctionMeterOutcome.Skipped ? 1L : 0L;
        var failed = succeeded == 0 && skipped == 0 ? 1L : 0L;
        return this with
        {
            SucceededAttemptCount = checked(SucceededAttemptCount + succeeded),
            FailedAttemptCount = checked(FailedAttemptCount + failed),
            SkippedCount = checked(SkippedCount + skipped),
            ExecutionDurationMilliseconds
            = checked(ExecutionDurationMilliseconds + meterEvent.ExecutionDurationMilliseconds.Value),
            UpdatedAtUtc = meterEvent.OccurredAtUtc,
        };
    }
}

internal static class FunctionDurableIdentity
{
    public static string Create(string kind, params string[] components)
    {
        var value = string.Join('\n', [kind, .. components]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

internal static class FunctionDurableModelGuard
{
    public static string NotBlank(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A value is required.", parameterName);
        return value;
    }

    public static int Positive(int value, string parameterName)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "A positive value is required.");
        return value;
    }

    public static int NonNegative(int value, string parameterName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameterName, "A non-negative value is required.");
        return value;
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("A UTC timestamp is required.", parameterName);
        return value;
    }

    public static FunctionRetryPolicy Retry(FunctionRetryPolicy retry)
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (retry.MaxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(retry), "Retry attempts must be positive.");
        if (retry.BackoffSeconds is null || retry.BackoffSeconds.Any(delay => delay < 0))
            throw new ArgumentException("Retry delays cannot be negative.", nameof(retry));
        return retry with { BackoffSeconds = retry.BackoffSeconds.ToImmutableList() };
    }

    public static void Dimensions(FunctionMeterDimensions dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        NotBlank(dimensions.TenantId, nameof(dimensions.TenantId));
        NotBlank(dimensions.FunctionAppId, nameof(dimensions.FunctionAppId));
        NotBlank(dimensions.RevisionId, nameof(dimensions.RevisionId));
        NotBlank(dimensions.TriggerId, nameof(dimensions.TriggerId));
        NotBlank(dimensions.HandlerName, nameof(dimensions.HandlerName));
    }
}
