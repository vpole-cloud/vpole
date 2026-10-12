using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pole.Features.Outbox.Application;

internal sealed class OutboxProcessingTriggerService(
    IOptions<OutboxSettings> options,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OutboxProcessingTriggerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.ProcessingTriggerInterval is not { } interval)
            return;

        // Resolve the dispatcher only when enabled: API slice hosts intentionally
        // leave the optional loop disabled and do not compose Hangfire storage.
        await using var scope = scopeFactory.CreateAsyncScope();
        var trigger = scope.ServiceProvider.GetRequiredService<IOutboxProcessingTrigger>();

        // PeriodicTimer retains at most one pending tick while dispatch is slow.
        // Await dispatch instead of adding a persistent job on every replica tick.
        using var timer = new PeriodicTimer(interval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await trigger.TriggerAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        exception,
                        "Could not dispatch pending outbox messages; the recurring schedule remains active");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown preserves pending messages for the next dispatcher or cron run.
        }
    }
}

internal interface IOutboxProcessingTrigger
{
    Task TriggerAsync(CancellationToken cancellationToken);
}

internal static class OutboxProcessingLock
{
    public const string Resource = "pole:outbox:processing";
}

internal sealed class DirectOutboxProcessingTrigger(
    JobStorage storage,
    IServiceScopeFactory scopeFactory) : IOutboxProcessingTrigger
{
    public Task TriggerAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Dispatch(cancellationToken), cancellationToken);

    private void Dispatch(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = storage.GetConnection();
        IDisposable processingLock;
        try
        {
            processingLock = connection.AcquireDistributedLock(
                OutboxProcessingLock.Resource,
                TimeSpan.Zero);
        }
        catch (DistributedLockTimeoutException)
        {
            // Another replica or the recurring processor already owns this batch.
            return;
        }

        using (processingLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scope = scopeFactory.CreateAsyncScope();
            try
            {
                // Hangfire.Mongo lock nesting and release are thread-affine. Keep
                // acquisition and disposal on this worker, as Hangfire does, even
                // when publishing resumes asynchronously on another thread.
                scope.ServiceProvider.GetRequiredService<OutboxProcessorJob>()
                    .ProcessOutboxMessagesAsync(cancellationToken)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }
}
