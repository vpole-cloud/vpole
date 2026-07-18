using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Timers;

namespace Pole.Features.Functions.Runtime.Metering;

public interface IFunctionUsageAggregator
{
    Task RunOnceAsync(CancellationToken cancellationToken);
}

public sealed class FunctionUsageAggregator(
    IFunctionAppDbContext dbContext,
    FunctionTimerOptions options,
    TimeProvider timeProvider,
    ILogger<FunctionUsageAggregator> logger) : IFunctionUsageAggregator
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var events = await dbContext.FunctionMeterEvents.Find(meter => meter.AggregatedAtUtc == null)
            .SortBy(meter => meter.OccurredAtUtcDateTime)
            .Limit(options.BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var meter in events)
        {
            try
            {
                await AggregateAsync(meter, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "[function-meter-aggregate-failed] Failed to aggregate meter event {MeterEventId}",
                    meter.Id);
            }
        }
    }

    private async Task AggregateAsync(FunctionMeterEvent meter, CancellationToken cancellationToken)
    {
        using var session = await dbContext.Client.StartSessionAsync(cancellationToken: cancellationToken);
        await session.WithTransactionAsync(async (transaction, transactionCancellationToken) =>
        {
            var aggregatedAtUtc = timeProvider.GetUtcNow();
            var marked = await dbContext.FunctionMeterEvents.UpdateOneAsync(
                transaction,
                candidate => candidate.Id == meter.Id && candidate.AggregatedAtUtc == null,
                Builders<FunctionMeterEvent>.Update.Set(
                    candidate => candidate.AggregatedAtUtc,
                    aggregatedAtUtc),
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
            {
                update = update.Inc(candidate => candidate.InvocationCount, 1);
            }
            else if (meter.Phase == FunctionMeterEventPhase.Started)
            {
                update = update.Inc(candidate => candidate.AttemptCount, 1);
            }
            else
            {
                update = update
                    .Inc(candidate => candidate.SucceededAttemptCount,
                        meter.Outcome == FunctionMeterOutcome.Succeeded ? 1 : 0)
                    .Inc(candidate => candidate.SkippedCount,
                        meter.Outcome == FunctionMeterOutcome.Skipped ? 1 : 0)
                    .Inc(candidate => candidate.FailedAttemptCount,
                        meter.Outcome is not (FunctionMeterOutcome.Succeeded or FunctionMeterOutcome.Skipped) ? 1 : 0)
                    .Inc(candidate => candidate.ExecutionDurationMilliseconds,
                        meter.ExecutionDurationMilliseconds ?? 0);
            }

            await dbContext.FunctionUsageBuckets.UpdateOneAsync(
                transaction,
                candidate => candidate.Id == bucket.Id,
                update,
                new UpdateOptions { IsUpsert = true },
                transactionCancellationToken);
            return true;
        }, cancellationToken: cancellationToken);
    }
}

public sealed class FunctionUsageAggregatorWorker(
    IFunctionUsageAggregator aggregator,
    FunctionTimerOptions options,
    TimeProvider timeProvider,
    ILogger<FunctionUsageAggregatorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SweepIntervalSeconds), timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await aggregator.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[function-meter-aggregate-cycle-failed] Meter aggregation failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
                break;
        }
    }
}
