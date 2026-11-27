// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.SourceBuilds.Application.Execution;

namespace Pole.Features.SourceBuilds.Application.Jobs;

internal sealed class SourceBuildHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task heartbeat;

    public SourceBuildHeartbeat(
        SourceBuildExecutionIdentity identity,
        RenewSourceBuildExecution renew,
        TimeProvider timeProvider) => heartbeat = RunAsync(identity, renew, timeProvider);

    public CancellationToken Token => cancellation.Token;

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        await heartbeat;
        cancellation.Dispose();
    }

    private async Task RunAsync(
        SourceBuildExecutionIdentity identity,
        RenewSourceBuildExecution renew,
        TimeProvider timeProvider)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20), timeProvider);
            while (await timer.WaitForNextTickAsync(cancellation.Token))
                if (!await renew(identity, cancellation.Token))
                {
                    await cancellation.CancelAsync();
                    return;
                }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch
        {
            // Stop external work on an uncertain lease; Mongo expiry permits safe recovery.
            await cancellation.CancelAsync();
        }
    }
}
