// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Net;

namespace Pole.Testing;

public static class TestWait
{
    public static async Task UntilAsync(
        Func<CancellationToken, Task<bool>> probe,
        TimeSpan timeout,
        string message,
        CancellationToken cancellationToken = default,
        TimeSpan? pollInterval = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                // Bound the awaited probe itself, including a transport that ignores cancellation.
                var pending = probe(deadline.Token);
                _ = pending.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                var complete = await pending.WaitAsync(deadline.Token);
                if (Stopwatch.GetElapsedTime(started) >= timeout)
                    throw new TimeoutException(message);
                if (complete)
                    return;
                await Task.Delay(interval, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(message);
        }
    }
}

public static class TestDeletion
{
    public static bool IsComplete(HttpStatusCode status)
    {
        if (status == HttpStatusCode.NotFound)
            return true;
        if ((int)status is >= 200 and < 300)
            return false;
        throw new HttpRequestException($"Deletion verification returned HTTP {(int)status}.", null, status);
    }
}
