using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Runtime.Timers;

public delegate Task MaterializeFunctionTimerOccurrence(
    FunctionTimerState claimed,
    CancellationToken cancellationToken = default);

public sealed class MongoMaterializeFunctionTimerOccurrence(
    IMongoClient mongoClient,
    IMongoCollection<FunctionTimerState> functionTimerStates,
    IMongoCollection<FunctionInvocation> functionInvocations,
    IMongoCollection<FunctionMeterEvent> functionMeterEvents,
    TimeProvider timeProvider)
{
    public async Task InvokeAsync(
        FunctionTimerState claimed,
        CancellationToken cancellationToken = default)
    {
        using var session = await mongoClient.StartSessionAsync(cancellationToken: cancellationToken);
        await session.WithTransactionAsync(async (transaction, transactionCancellationToken) =>
        {
            var now = timeProvider.GetUtcNow();
            var timer = await functionTimerStates.Find(
                    transaction,
                    FunctionTimerOccurrenceFilters.ClaimedTimer(claimed))
                .FirstOrDefaultAsync(transactionCancellationToken);
            if (timer is null || timer.Lease is null || !timer.Lease.IsActiveAt(now))
                return true;

            var schedule = FunctionCronSchedule.Parse(timer.Cron);
            var scheduledFor = timer.NextOccurrenceUtc;
            var nextAfterScheduled = schedule.GetNextOccurrence(scheduledFor);
            var isMisfire = nextAfterScheduled <= now;
            var nextOccurrence = isMisfire
                ? schedule.GetNextOccurrence(now)
                : nextAfterScheduled;

            if (!(isMisfire && timer.MisfirePolicy == TimerMisfirePolicy.Skip))
            {
                var missedOccurrences = isMisfire
                    ? CountMissedOccurrences(schedule, nextAfterScheduled, now)
                    : 0;
                var invocation = FunctionInvocation.CreateTimer(
                    timer.TenantId,
                    timer.FunctionAppId,
                    timer.RevisionId,
                    timer.TriggerId,
                    timer.HandlerName,
                    timer.ScheduleVersion,
                    scheduledFor,
                    timer.Retry,
                    now,
                    isPastDue: isMisfire,
                    missedOccurrenceCount: missedOccurrences);

                var existing = await functionInvocations.Find(
                        transaction,
                        FunctionTimerOccurrenceFilters.InvocationById(invocation.Id))
                    .FirstOrDefaultAsync(transactionCancellationToken);
                if (existing is null)
                {
                    FunctionMeterEvent? skippedMeter = null;
                    if (timer.ConcurrencyPolicy == TimerConcurrencyPolicy.Forbid &&
                        await functionInvocations.Find(
                                transaction,
                                FunctionTimerOccurrenceFilters.ActiveSiblingInvocation(timer, invocation.Id))
                            .AnyAsync(transactionCancellationToken))
                    {
                        invocation = invocation.Skip(
                            FunctionInvocationError.Create(
                                "timer_concurrency_forbid",
                                "The previous Timer invocation is still active.",
                                retryable: false),
                            now);
                        skippedMeter = FunctionMeterEvent.Completed(
                            invocation,
                            FunctionMeterOutcome.Skipped,
                            executionDurationMilliseconds: 0,
                            now);
                    }

                    await functionInvocations.InsertOneAsync(
                        transaction,
                        invocation,
                        cancellationToken: transactionCancellationToken);
                    var meters = skippedMeter is null
                        ? new[] { FunctionMeterEvent.Admitted(invocation, now) }
                        : new[] { FunctionMeterEvent.Admitted(invocation, now), skippedMeter };
                    await functionMeterEvents.InsertManyAsync(
                        transaction,
                        meters,
                        cancellationToken: transactionCancellationToken);
                }
            }

            var advanced = await functionTimerStates.UpdateOneAsync(
                transaction,
                FunctionTimerOccurrenceFilters.Advance(timer, scheduledFor),
                Builders<FunctionTimerState>.Update
                    .Set(candidate => candidate.NextOccurrenceUtc, nextOccurrence)
                    .Set(candidate => candidate.Lease, null)
                    .Set(candidate => candidate.UpdatedAtUtc, now),
                cancellationToken: transactionCancellationToken);
            if (advanced.MatchedCount != 1)
                throw new InvalidOperationException("Function Timer state changed while materializing an occurrence.");
            return true;
        }, cancellationToken: cancellationToken);
    }

    private static int CountMissedOccurrences(
        FunctionCronSchedule schedule,
        DateTimeOffset firstMissedUtc,
        DateTimeOffset nowUtc)
    {
        const int maximumCount = 100_000;
        var count = 0;
        for (var occurrence = firstMissedUtc;
             occurrence <= nowUtc && count < maximumCount;
             occurrence = schedule.GetNextOccurrence(occurrence))
            count++;
        return count;
    }
}

internal static class FunctionTimerOccurrenceFilters
{
    public static FilterDefinition<FunctionTimerState> ClaimedTimer(FunctionTimerState claimed) =>
        Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.Id, claimed.Id),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.TenantId, claimed.TenantId),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.FunctionAppId, claimed.FunctionAppId),
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.Enabled, true),
            Builders<FunctionTimerState>.Filter.Ne(timer => timer.Lease, null),
            Builders<FunctionTimerState>.Filter.Eq("Lease.Token", claimed.Lease!.Token));

    public static FilterDefinition<FunctionInvocation> InvocationById(string invocationId) =>
        Builders<FunctionInvocation>.Filter.Eq(invocation => invocation.Id, invocationId);

    public static FilterDefinition<FunctionInvocation> ActiveSiblingInvocation(
        FunctionTimerState timer,
        string invocationId) =>
        Builders<FunctionInvocation>.Filter.And(
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.TenantId, timer.TenantId),
            Builders<FunctionInvocation>.Filter.Eq(candidate => candidate.TriggerId, timer.TriggerId),
            Builders<FunctionInvocation>.Filter.Ne(candidate => candidate.Id, invocationId),
            Builders<FunctionInvocation>.Filter.In(
                candidate => candidate.State,
                [
                    FunctionInvocationState.Pending,
                    FunctionInvocationState.Dispatching,
                    FunctionInvocationState.Running,
                    FunctionInvocationState.RetryScheduled
                ]));

    public static FilterDefinition<FunctionTimerState> Advance(
        FunctionTimerState timer,
        DateTimeOffset scheduledForUtc) =>
        Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(candidate => candidate.Id, timer.Id),
            Builders<FunctionTimerState>.Filter.Ne(candidate => candidate.Lease, null),
            Builders<FunctionTimerState>.Filter.Eq("Lease.Token", timer.Lease!.Token),
            Builders<FunctionTimerState>.Filter.Eq(candidate => candidate.NextOccurrenceUtc, scheduledForUtc));
}
