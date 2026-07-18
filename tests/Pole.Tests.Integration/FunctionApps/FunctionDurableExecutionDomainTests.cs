using MongoDB.Bson;
using Pole.Features.Functions.Domain;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionDurableExecutionDomainTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-18T10:00:00Z");

    [Fact]
    public void Timer_state_pins_revision_and_advances_only_under_its_active_lease()
    {
        var timer = CreateTimer();
        var leased = timer.AcquireLease("scheduler-1", "lease-1", Now, Now.AddSeconds(30));
        var advanced = leased.Advance("lease-1", Now.AddMinutes(10), Now.AddSeconds(2));

        Assert.True(timer.HasValidState());
        Assert.Equal("revision-1", timer.RevisionId);
        Assert.Equal(3, timer.ScheduleVersion);
        Assert.Equal(FunctionTimerState.UtcTimeZone, timer.TimeZone);
        Assert.Null(advanced.Lease);
        Assert.Equal(Now.AddMinutes(10), advanced.NextOccurrenceUtc);
        Assert.Throws<InvalidOperationException>(() => leased.Advance(
            "another-lease",
            Now.AddMinutes(10),
            Now.AddSeconds(2)));
        Assert.Throws<InvalidOperationException>(() => leased.Advance(
            "lease-1",
            Now.AddMinutes(10),
            Now.AddMinutes(1)));
    }

    [Fact]
    public void Timer_reconcile_is_idempotent_but_rejects_version_reuse_with_different_configuration()
    {
        var timer = CreateTimer();
        var unchanged = timer.Reconcile(
            timer.RevisionId,
            timer.HandlerName,
            timer.Cron,
            timer.ScheduleVersion,
            timer.ConcurrencyPolicy,
            timer.MisfirePolicy,
            timer.Retry,
            timer.NextOccurrenceUtc,
            Now.AddSeconds(1));

        Assert.Equal(timer.Id, unchanged.Id);
        var repinned = timer.Reconcile(
            "revision-2",
            timer.HandlerName,
            timer.Cron,
            timer.ScheduleVersion,
            timer.ConcurrencyPolicy,
            timer.MisfirePolicy,
            new FunctionRetryPolicy(4, [5, 30, 60]),
            timer.NextOccurrenceUtc,
            Now.AddSeconds(1));
        Assert.Equal("revision-2", repinned.RevisionId);
        Assert.Equal(4, repinned.Retry.MaxAttempts);
        Assert.Throws<InvalidOperationException>(() => timer.Reconcile(
            timer.RevisionId,
            timer.HandlerName,
            "*/10 * * * *",
            timer.ScheduleVersion,
            timer.ConcurrencyPolicy,
            timer.MisfirePolicy,
            timer.Retry,
            timer.NextOccurrenceUtc,
            Now.AddSeconds(1)));
    }

    [Fact]
    public void Invocation_retry_keeps_logical_identity_and_increments_attempt_when_due()
    {
        var invocation = CreateInvocation();
        var dispatching = invocation.AcquireDispatchLease("dispatcher-1", "lease-1", Now, Now.AddMinutes(1));
        var running = dispatching.MarkRunning("lease-1", Now.AddSeconds(1));
        var retryAt = Now.AddSeconds(10);
        var retrying = running.ScheduleRetry(
            "lease-1",
            retryAt,
            FunctionInvocationError.Create("temporary", "Try again.", retryable: true),
            Now.AddSeconds(2));

        Assert.Equal(invocation.Id, retrying.Id);
        Assert.Equal(1, retrying.AttemptNumber);
        Assert.Equal(FunctionInvocationState.RetryScheduled, retrying.State);
        Assert.True(retrying.State.IsNonTerminal());
        Assert.True(retrying.HasValidState());
        Assert.Throws<InvalidOperationException>(() => retrying.AcquireDispatchLease(
            "dispatcher-2",
            "lease-2",
            Now.AddSeconds(5),
            Now.AddMinutes(1)));

        var secondAttempt = retrying.AcquireDispatchLease(
            "dispatcher-2",
            "lease-2",
            retryAt,
            retryAt.AddMinutes(1));

        Assert.Equal(2, secondAttempt.AttemptNumber);
        Assert.Equal(FunctionInvocationState.Dispatching, secondAttempt.State);
        Assert.True(secondAttempt.HasValidState());
    }

    [Fact]
    public void Invocation_requires_lease_ownership_and_terminal_states_cannot_be_redispatched()
    {
        var running = CreateInvocation()
            .AcquireDispatchLease("dispatcher-1", "lease-1", Now, Now.AddMinutes(1))
            .MarkRunning("lease-1", Now.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() => running.Succeed("lease-2", Now.AddSeconds(2)));

        var succeeded = running.Succeed("lease-1", Now.AddSeconds(2));

        Assert.True(succeeded.State.IsTerminal());
        Assert.False(succeeded.State.IsNonTerminal());
        Assert.True(succeeded.HasValidState());
        Assert.Throws<InvalidOperationException>(() => succeeded.AcquireDispatchLease(
            "dispatcher-2",
            "lease-2",
            Now.AddSeconds(3),
            Now.AddMinutes(1)));
    }

    [Fact]
    public void Invocation_identity_is_deterministic_for_one_scheduled_occurrence()
    {
        var first = CreateInvocation();
        var replay = CreateInvocation();
        var nextOccurrence = FunctionInvocation.CreateTimer(
            "tenant-1",
            "app-1",
            "revision-1",
            "trigger-1",
            "cleanup",
            3,
            Now.AddMinutes(6),
            new FunctionRetryPolicy(3, [5, 30]),
            Now);

        Assert.Equal(first.Id, replay.Id);
        Assert.NotEqual(first.Id, nextOccurrence.Id);
    }

    [Fact]
    public void Meter_events_are_deterministic_per_attempt_and_phase()
    {
        var running = CreateInvocation()
            .AcquireDispatchLease("dispatcher-1", "lease-1", Now, Now.AddMinutes(1))
            .MarkRunning("lease-1", Now.AddSeconds(1));
        var first = FunctionMeterEvent.Started(running, Now.AddSeconds(1));
        var replay = FunctionMeterEvent.Started(running, Now.AddSeconds(2));
        var succeeded = running.Succeed("lease-1", Now.AddSeconds(3));
        var completed = FunctionMeterEvent.Completed(
            succeeded,
            FunctionMeterOutcome.Succeeded,
            executionDurationMilliseconds: 2_000,
            Now.AddSeconds(3));

        Assert.Equal(first.Id, replay.Id);
        Assert.NotEqual(first.Id, completed.Id);
        Assert.Equal(FunctionMeterTriggerKind.Timer, completed.TriggerKind);
        Assert.DoesNotContain("tenant-1", completed.Id, StringComparison.Ordinal);
        Assert.NotEqual(FunctionMeterEvent.Admitted(running, Now).Id, first.Id);
    }

    [Fact]
    public void Usage_bucket_preserves_dimensions_and_counts_started_and_completed_events()
    {
        var running = CreateInvocation()
            .AcquireDispatchLease("dispatcher-1", "lease-1", Now, Now.AddMinutes(1))
            .MarkRunning("lease-1", Now.AddSeconds(1));
        var started = FunctionMeterEvent.Started(running, Now.AddSeconds(1));
        var admitted = FunctionMeterEvent.Admitted(running, Now);
        var succeeded = running.Succeed("lease-1", Now.AddSeconds(3));
        var completed = FunctionMeterEvent.Completed(
            succeeded,
            FunctionMeterOutcome.Succeeded,
            executionDurationMilliseconds: 2_000,
            Now.AddSeconds(3));
        var bucket = FunctionUsageBucket.Create(started.Dimensions, FunctionUsageBucket.ToHour(started.OccurredAtUtc));

        var usage = bucket.Apply(admitted).Apply(started).Apply(completed);

        Assert.Equal(started.Dimensions.TenantId, usage.TenantId);
        Assert.Equal(started.Dimensions.FunctionAppId, usage.FunctionAppId);
        Assert.Equal(started.Dimensions.RevisionId, usage.RevisionId);
        Assert.Equal(started.Dimensions.TriggerId, usage.TriggerId);
        Assert.Equal(started.Dimensions.HandlerName, usage.HandlerName);
        Assert.Equal(1, usage.InvocationCount);
        Assert.Equal(1, usage.AttemptCount);
        Assert.Equal(1, usage.SucceededAttemptCount);
        Assert.Equal(0, usage.FailedAttemptCount);
        Assert.Equal(2_000, usage.ExecutionDurationMilliseconds);
    }

    [Fact]
    public void Utc_boundaries_are_enforced_by_factories()
    {
        var nonUtc = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(() => FunctionInvocation.CreateTimer(
            "tenant-1",
            "app-1",
            "revision-1",
            "trigger-1",
            "cleanup",
            3,
            nonUtc,
            new FunctionRetryPolicy(3, [5, 30]),
            Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => FunctionUsageBucket.Create(
            new FunctionMeterDimensions(
                "tenant-1",
                "app-1",
                "revision-1",
                "trigger-1",
                "cleanup",
                FunctionMeterTriggerKind.Timer),
            Now.AddMinutes(1)));
    }

    [Fact]
    public void Meter_event_exposes_a_bson_date_for_ttl_retention()
    {
        var meter = FunctionMeterEvent.Admitted(CreateInvocation(), Now);

        var document = meter.ToBsonDocument();

        Assert.Equal(BsonType.DateTime, document[nameof(FunctionMeterEvent.OccurredAtUtcDateTime)].BsonType);
        Assert.Equal(Now.UtcDateTime, meter.OccurredAtUtcDateTime);
    }

    private static FunctionTimerState CreateTimer() =>
        FunctionTimerState.Create(
            "tenant-1",
            "app-1",
            "revision-1",
            "trigger-1",
            "cleanup",
            "*/5 * * * *",
            3,
            TimerConcurrencyPolicy.Forbid,
            TimerMisfirePolicy.FireOnce,
            new FunctionRetryPolicy(3, [5, 30]),
            Now.AddMinutes(5),
            Now);

    private static FunctionInvocation CreateInvocation() =>
        FunctionInvocation.CreateTimer(
            "tenant-1",
            "app-1",
            "revision-1",
            "trigger-1",
            "cleanup",
            3,
            Now.AddMinutes(5),
            new FunctionRetryPolicy(3, [5, 30]),
            Now);
}
