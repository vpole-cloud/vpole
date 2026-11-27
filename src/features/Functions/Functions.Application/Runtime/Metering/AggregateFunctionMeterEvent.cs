// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Runtime.Metering;

public delegate Task AggregateFunctionMeterEvent(
    FunctionMeterEvent meter,
    CancellationToken cancellationToken = default);

public sealed class MongoAggregateFunctionMeterEvent(
    IMongoClient mongoClient,
    IMongoCollection<FunctionMeterEvent> functionMeterEvents,
    IMongoCollection<FunctionUsageBucket> functionUsageBuckets,
    TimeProvider timeProvider
)
{
    public async Task InvokeAsync(FunctionMeterEvent meter, CancellationToken cancellationToken = default)
    {
        using var session = await mongoClient.StartSessionAsync(cancellationToken: cancellationToken);
        await session.WithTransactionAsync(
            async (transaction, transactionCancellationToken) =>
            {
                var aggregatedAtUtc = timeProvider.GetUtcNow();
                var marked = await functionMeterEvents.UpdateOneAsync(
                    transaction,
                    FunctionMeterAggregationFilters.UnaggregatedById(meter.Id),
                    Builders<FunctionMeterEvent>.Update.Set(candidate => candidate.AggregatedAtUtc, aggregatedAtUtc),
                    cancellationToken: transactionCancellationToken);
                if (marked.ModifiedCount != 1)
                    return true;

                var hour = FunctionUsageBucket.ToHour(meter.OccurredAtUtc);
                var bucket = FunctionUsageBucket.Create(meter.Dimensions, hour);
                var update = Builders<FunctionUsageBucket>.Update
                    .SetOnInsert(candidate => candidate.Id, bucket.Id)
                    .SetOnInsert(candidate => candidate.TenantId, bucket.TenantId)
                    .SetOnInsert(candidate => candidate.FunctionAppId, bucket.FunctionAppId)
                    .SetOnInsert(candidate => candidate.RevisionId, bucket.RevisionId)
                    .SetOnInsert(candidate => candidate.TriggerId, bucket.TriggerId)
                    .SetOnInsert(candidate => candidate.HandlerName, bucket.HandlerName)
                    .SetOnInsert(candidate => candidate.TriggerKind, bucket.TriggerKind)
                    .SetOnInsert(candidate => candidate.HourUtc, bucket.HourUtc)
                    .Set(candidate => candidate.UpdatedAtUtc, aggregatedAtUtc);
                if (meter.Phase == FunctionMeterEventPhase.Admitted)
                    update = update.Inc(candidate => candidate.InvocationCount, 1);
                else if (meter.Phase == FunctionMeterEventPhase.Started)
                    update = update.Inc(candidate => candidate.AttemptCount, 1);
                else
                    update = update
                        .Inc(
                            candidate => candidate.SucceededAttemptCount,
                            meter.Outcome == FunctionMeterOutcome.Succeeded ? 1 : 0)
                        .Inc(candidate => candidate.SkippedCount, meter.Outcome == FunctionMeterOutcome.Skipped ? 1 : 0)
                        .Inc(
                            candidate => candidate.FailedAttemptCount,
                            meter.Outcome is not (FunctionMeterOutcome.Succeeded or FunctionMeterOutcome.Skipped)
                                ? 1
                                : 0)
                        .Inc(
                            candidate => candidate.ExecutionDurationMilliseconds,
                            meter.ExecutionDurationMilliseconds ?? 0);

                await functionUsageBuckets.UpdateOneAsync(
                    transaction,
                    FunctionMeterAggregationFilters.ById(bucket.Id),
                    update,
                    new UpdateOptions { IsUpsert = true },
                    transactionCancellationToken);
                return true;
            },
            cancellationToken: cancellationToken);
    }
}
