// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pole.Features.Functions.Application.Runtime.Timers;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Runtime.Timers;

public sealed class FunctionTimerOptions
{
    public const string SectionName = "Functions:Timers";

    [Range(1, 60)] public int SweepIntervalSeconds { get; set; } = 1;

    [Range(5, 300)] public int ScheduleLeaseSeconds { get; set; } = 30;

    [Range(10, 3_600)] public int DispatchLeaseSeconds { get; set; } = 960;

    [Range(1, 86_400)] public int ExecutionTimeoutSeconds { get; set; } = 900;

    [Range(1, 500)] public int BatchSize { get; set; } = 50;
}

public sealed class FunctionTimerOptionsValidator : IValidateOptions<FunctionTimerOptions>
{
    public ValidateOptionsResult Validate(string? name, FunctionTimerOptions options)
    {
        var failures = FunctionOptionsValidation.GetDataAnnotationFailures(options);
        if (options.DispatchLeaseSeconds <= options.ExecutionTimeoutSeconds)
            failures.Add(
                $"{nameof(options.DispatchLeaseSeconds)} must exceed {nameof(options.ExecutionTimeoutSeconds)}.");
        if (options.DispatchLeaseSeconds <= FunctionScalingPolicy.MaximumActivationTimeoutSeconds)
            failures.Add(
                $"{nameof(options.DispatchLeaseSeconds)} must exceed the maximum Function activation timeout " +
                $"of {FunctionScalingPolicy.MaximumActivationTimeoutSeconds} seconds.");

        return FunctionOptionsValidation.ToValidateOptionsResult(failures);
    }
}

public interface IFunctionTimerScheduler
{
    Task RunOnceAsync(CancellationToken cancellationToken);
}

public sealed class FunctionTimerScheduler(
    ClaimNextFunctionTimer claimNext,
    MaterializeFunctionTimerOccurrence materializeOccurrence,
    ReleaseFunctionTimerLease releaseLease,
    FunctionTimerOptions options,
    ILogger<FunctionTimerScheduler> logger
) : IFunctionTimerScheduler
{
    private readonly string ownerId = $"timer-scheduler-{Guid.NewGuid():N}";

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < options.BatchSize; index++)
        {
            var claimed = await claimNext(ownerId, options.ScheduleLeaseSeconds, cancellationToken);
            if (claimed is null)
                return;

            try
            {
                await materializeOccurrence(claimed, cancellationToken);
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
                await releaseLease(claimed, cancellationToken);
            }
        }
    }
}

public sealed class FunctionTimerSchedulerWorker(
    IFunctionTimerScheduler scheduler,
    FunctionTimerOptions options,
    TimeProvider timeProvider,
    ILogger<FunctionTimerSchedulerWorker> logger
) : BackgroundService
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
