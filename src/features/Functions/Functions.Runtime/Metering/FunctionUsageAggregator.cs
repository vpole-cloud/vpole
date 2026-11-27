// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pole.Features.Functions.Application.Runtime.Metering;
using Pole.Features.Functions.Runtime.Timers;

namespace Pole.Features.Functions.Runtime.Metering;

public interface IFunctionUsageAggregator
{
    Task RunOnceAsync(CancellationToken cancellationToken);
}

public sealed class FunctionUsageAggregator(
    LoadUnaggregatedFunctionMeterEvents loadUnaggregated,
    AggregateFunctionMeterEvent aggregateMeter,
    FunctionTimerOptions options,
    ILogger<FunctionUsageAggregator> logger
) : IFunctionUsageAggregator
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var events = await loadUnaggregated(options.BatchSize, cancellationToken);
        foreach (var meter in events)
            try
            {
                await aggregateMeter(meter, cancellationToken);
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

public sealed class FunctionUsageAggregatorWorker(
    IFunctionUsageAggregator aggregator,
    FunctionTimerOptions options,
    TimeProvider timeProvider,
    ILogger<FunctionUsageAggregatorWorker> logger
) : BackgroundService
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
