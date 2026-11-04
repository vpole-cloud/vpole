using Pole.Features.SourceBuilds.Application.Persistence.Jobs;

namespace Pole.Features.SourceBuilds.Application.Jobs;

internal sealed class SourceBuildHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task heartbeat;
    public CancellationToken Token => cancellation.Token;

    public SourceBuildHeartbeat(SourceBuildExecutionIdentity identity, RenewSourceBuildExecution renew,
        TimeProvider timeProvider)
    {
        heartbeat = RunAsync(identity, renew, timeProvider);
    }

    private async Task RunAsync(SourceBuildExecutionIdentity identity, RenewSourceBuildExecution renew,
        TimeProvider timeProvider)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20), timeProvider);
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                if (!await renew(identity, cancellation.Token))
                {
                    await cancellation.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch
        {
            // Stop external work on an uncertain lease; Mongo expiry permits safe recovery.
            await cancellation.CancelAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        await heartbeat;
        cancellation.Dispose();
    }
}
