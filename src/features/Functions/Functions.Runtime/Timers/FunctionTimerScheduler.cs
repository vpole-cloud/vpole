using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Runtime.Timers;

public sealed class FunctionTimerOptions
{
    public const string SectionName = "Functions:Timers";

    [Range(1, 60)]
    public int SweepIntervalSeconds { get; set; } = 1;

    [Range(5, 300)]
    public int ScheduleLeaseSeconds { get; set; } = 30;

    [Range(10, 3_600)]
    public int DispatchLeaseSeconds { get; set; } = 960;

    [Range(1, 86_400)]
    public int ExecutionTimeoutSeconds { get; set; } = 900;

    [Range(1, 500)]
    public int BatchSize { get; set; } = 50;
}

public sealed class FunctionTimerOptionsValidator : IValidateOptions<FunctionTimerOptions>
{
    public ValidateOptionsResult Validate(string? name, FunctionTimerOptions options)
    {
        var failures = FunctionOptionsValidation.GetDataAnnotationFailures(options);
        if (options.DispatchLeaseSeconds <= options.ExecutionTimeoutSeconds)
        {
            failures.Add(
                $"{nameof(options.DispatchLeaseSeconds)} must exceed {nameof(options.ExecutionTimeoutSeconds)}.");
        }
        if (options.DispatchLeaseSeconds <= FunctionScalingPolicy.MaximumActivationTimeoutSeconds)
        {
            failures.Add(
                $"{nameof(options.DispatchLeaseSeconds)} must exceed the maximum Function activation timeout " +
                $"of {FunctionScalingPolicy.MaximumActivationTimeoutSeconds} seconds.");
        }

        return FunctionOptionsValidation.ToValidateOptionsResult(failures);
    }
}

public interface IFunctionTimerScheduler
{
    Task RunOnceAsync(CancellationToken cancellationToken);
}

public sealed class FunctionTimerScheduler(
    IFunctionAppDbContext dbContext,
    FunctionTimerOptions options,
    TimeProvider timeProvider,
    ILogger<FunctionTimerScheduler> logger) : IFunctionTimerScheduler
{
    private readonly string ownerId = $"timer-scheduler-{Guid.NewGuid():N}";

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < options.BatchSize; index++)
        {
            var claimed = await ClaimNextAsync(cancellationToken);
            if (claimed is null)
                return;

            try
            {
                await MaterializeOccurrenceAsync(claimed, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "[function-timer-materialize-failed] Failed to materialize Timer {TriggerId}",
                    claimed.TriggerId);
                await ReleaseLeaseAsync(claimed, cancellationToken);
            }
        }
    }

    private async Task<FunctionTimerState?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var lease = FunctionExecutionLease.Create(
            ownerId,
            Guid.NewGuid().ToString("N"),
            now,
            now.AddSeconds(options.ScheduleLeaseSeconds));
        var filter = Builders<FunctionTimerState>.Filter.And(
            Builders<FunctionTimerState>.Filter.Eq(timer => timer.Enabled, true),
            Builders<FunctionTimerState>.Filter.Lte(timer => timer.NextOccurrenceUtc, now),
            Builders<FunctionTimerState>.Filter.Or(
                Builders<FunctionTimerState>.Filter.Eq(timer => timer.Lease, null),
                Builders<FunctionTimerState>.Filter.Lte("Lease.ExpiresAtUtc", now)));
        return await dbContext.FunctionTimerStates.FindOneAndUpdateAsync(
            filter,
            Builders<FunctionTimerState>.Update
                .Set(timer => timer.Lease, lease)
                .Set(timer => timer.UpdatedAtUtc, now),
            new FindOneAndUpdateOptions<FunctionTimerState>
            {
                ReturnDocument = ReturnDocument.After,
                Sort = Builders<FunctionTimerState>.Sort.Ascending(timer => timer.NextOccurrenceUtc)
            },
            cancellationToken);
    }

    private async Task MaterializeOccurrenceAsync(
        FunctionTimerState claimed,
        CancellationToken cancellationToken)
    {
        using var session = await dbContext.Client.StartSessionAsync(cancellationToken: cancellationToken);
        await session.WithTransactionAsync(async (transaction, transactionCancellationToken) =>
        {
            var now = timeProvider.GetUtcNow();
            var timer = await dbContext.FunctionTimerStates.Find(
                    transaction,
                    candidate => candidate.Id == claimed.Id &&
                                 candidate.TenantId == claimed.TenantId &&
                                 candidate.FunctionAppId == claimed.FunctionAppId &&
                                 candidate.Enabled &&
                                 candidate.Lease != null &&
                                 candidate.Lease.Token == claimed.Lease!.Token)
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

                var existing = await dbContext.FunctionInvocations.Find(
                        transaction,
                        candidate => candidate.Id == invocation.Id)
                    .FirstOrDefaultAsync(transactionCancellationToken);
                if (existing is null)
                {
                    FunctionMeterEvent? skippedMeter = null;
                    if (timer.ConcurrencyPolicy == TimerConcurrencyPolicy.Forbid)
                    {
                        var hasActiveInvocation = await dbContext.FunctionInvocations.Find(
                                transaction,
                                candidate => candidate.TenantId == timer.TenantId &&
                                             candidate.TriggerId == timer.TriggerId &&
                                             candidate.Id != invocation.Id &&
                                             (candidate.State == FunctionInvocationState.Pending ||
                                              candidate.State == FunctionInvocationState.Dispatching ||
                                              candidate.State == FunctionInvocationState.Running ||
                                              candidate.State == FunctionInvocationState.RetryScheduled))
                            .AnyAsync(transactionCancellationToken);
                        if (hasActiveInvocation)
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
                    }

                    await dbContext.FunctionInvocations.InsertOneAsync(
                        transaction,
                        invocation,
                        cancellationToken: transactionCancellationToken);
                    var meters = skippedMeter is null
                        ? new[] { FunctionMeterEvent.Admitted(invocation, now) }
                        : new[] { FunctionMeterEvent.Admitted(invocation, now), skippedMeter };
                    await dbContext.FunctionMeterEvents.InsertManyAsync(
                        transaction,
                        meters,
                        cancellationToken: transactionCancellationToken);
                }
            }

            var advanced = await dbContext.FunctionTimerStates.UpdateOneAsync(
                transaction,
                candidate => candidate.Id == timer.Id &&
                             candidate.Lease != null &&
                             candidate.Lease.Token == timer.Lease.Token &&
                             candidate.NextOccurrenceUtc == scheduledFor,
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

    private async Task ReleaseLeaseAsync(FunctionTimerState timer, CancellationToken cancellationToken)
    {
        if (timer.Lease is null)
            return;
        var now = timeProvider.GetUtcNow();
        await dbContext.FunctionTimerStates.UpdateOneAsync(
            candidate => candidate.Id == timer.Id &&
                         candidate.Lease != null &&
                         candidate.Lease.Token == timer.Lease.Token,
            Builders<FunctionTimerState>.Update
                .Set(candidate => candidate.Lease, null)
                .Set(candidate => candidate.UpdatedAtUtc, now),
            cancellationToken: cancellationToken);
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

public sealed class FunctionTimerSchedulerWorker(
    IFunctionTimerScheduler scheduler,
    FunctionTimerOptions options,
    TimeProvider timeProvider,
    ILogger<FunctionTimerSchedulerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SweepIntervalSeconds), timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await scheduler.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[function-timer-sweep-failed] Function Timer sweep failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
                break;
        }
    }
}
