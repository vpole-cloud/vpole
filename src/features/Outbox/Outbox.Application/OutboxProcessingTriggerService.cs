using Hangfire;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pole.Features.Outbox.Application;

internal sealed class OutboxProcessingTriggerService(
    IOptions<OutboxSettings> options,
    IOutboxProcessingTrigger trigger,
    ILogger<OutboxProcessingTriggerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.ProcessingTriggerInterval is not { } interval)
            return;

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                trigger.Trigger();
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    exception,
                    "Could not trigger the outbox processor job; the recurring schedule remains active");
            }
        }
    }
}

internal interface IOutboxProcessingTrigger
{
    void Trigger();
}

internal sealed class HangfireOutboxProcessingTrigger : IOutboxProcessingTrigger
{
    public void Trigger() => RecurringJob.TriggerJob(OutboxFeatureRegistration.ProcessingJobId);
}
