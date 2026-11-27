// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Threading.Channels;

namespace Pole.Observation;

/// <summary>Negative transitions fence storage immediately; positive hints coalesce behind one native read.</summary>
public sealed class ObservationOwner(
    IObservationSession session,
    TimeProvider clock
)
{
    public async Task RunAsync(long initialVersion, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(1);
        var hints = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            });
        SourceState state = new Disconnected(initialVersion);
        var tasks = new[] { PumpAsync(), SampleAsync(), RenewAsync(), ConfirmAsync() };
        try
        {
            await await Task.WhenAny(tasks);
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                if (state is Connected connected)
                {
                    await connected.Lifetime.CancelAsync();
                    connected.Lifetime.Dispose();
                }
            }
        }

        async Task SetSourceAsync(
            string? sourceId,
            long? expectedVersion = null,
            bool stop = false)
        {
            await gate.WaitAsync(lifetime.Token);
            try
            {
                // A failed old sample cannot withdraw a newly recovered source.
                if (expectedVersion is not null && state.Version != expectedVersion)
                    return;
                var version = await session.SetSourceAsync(state.Version, sourceId, lifetime.Token);
                if (version is null)
                {
                    await lifetime.CancelAsync();
                    return;
                }

                if (state.Version == version && (state as Connected)?.Cut.SourceId == sourceId)
                    return;
                if (state is Connected != sourceId is not null)
                    session.RecordSource(sourceId is not null);
                if (state is Connected previous)
                {
                    await previous.Lifetime.CancelAsync();
                    previous.Lifetime.Dispose();
                }

                state = sourceId is null
                    ? new Disconnected(version.Value)
                    : new Connected(
                        new ObservationCut(version.Value, sourceId),
                        CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token));
                if (sourceId is not null)
                    hints.Writer.TryWrite(true);
                if (stop)
                    await lifetime.CancelAsync();
            }
            finally
            {
                gate.Release();
            }
        }

        async Task PumpAsync()
        {
            try
            {
                await foreach (var change in session.WatchAsync(lifetime.Token))
                    switch (change)
                    {
                        case ObservationSourceReady ready:
                            await SetSourceAsync(ready.SourceId);
                            break;
                        case ObservationSourceLost:
                            await SetSourceAsync(null);
                            break;
                        case ObservationInvalidated invalidated:
                            await gate.WaitAsync(lifetime.Token);
                            bool matches;
                            try
                            {
                                matches = invalidated.SourceId is null || (state is Connected connected &&
                                    connected.Cut.SourceId == invalidated.SourceId);
                                if (matches)
                                    hints.Writer.TryWrite(true);
                            }
                            finally
                            {
                                gate.Release();
                            }

                            if (!matches)
                                await SetSourceAsync(null);
                            break;
                    }
            }
            finally
            {
                await lifetime.CancelAsync();
            }
        }

        async Task SampleAsync()
        {
            while (await hints.Reader.WaitToReadAsync(lifetime.Token))
            {
                await Task.Delay(ObservationPolicy.CoalesceInterval, clock, lifetime.Token);
                while (hints.Reader.TryRead(out _))
                {
                }

                Connected? captured;
                CancellationToken sampleToken;
                await gate.WaitAsync(lifetime.Token);
                try
                {
                    captured = state as Connected;
                    sampleToken = captured?.Lifetime.Token ?? lifetime.Token;
                }
                finally
                {
                    gate.Release();
                }

                if (captured is null)
                    continue;
                var started = Stopwatch.GetTimestamp();
                var outcome = ObservationSampleMetric.Failed;
                try
                {
                    var result = await session.SampleAsync(captured.Cut, sampleToken);
                    outcome = result == ObservationSampleResult.Accepted
                        ? ObservationSampleMetric.Accepted
                        : ObservationSampleMetric.Rejected;
                    if (result is ObservationSampleResult.BindingLost or ObservationSampleResult.Unavailable)
                        await SetSourceAsync(null, captured.Version, result == ObservationSampleResult.Unavailable);
                    else if (result == ObservationSampleResult.OwnerLost)
                        await lifetime.CancelAsync();
                }
                catch (OperationCanceledException) when (sampleToken.IsCancellationRequested &&
                                                         !lifetime.IsCancellationRequested)
                {
                    outcome = ObservationSampleMetric.Cancelled;
                }
                catch (OperationCanceledException)
                {
                    outcome = ObservationSampleMetric.Cancelled;
                    throw;
                }
                finally
                {
                    session.RecordSample(outcome, Stopwatch.GetElapsedTime(started));
                }
            }
        }

        async Task RenewAsync()
        {
            while (true)
            {
                await Task.Delay(ObservationPolicy.RenewalInterval, clock, lifetime.Token);
                if (!await session.RenewAsync(lifetime.Token))
                {
                    await lifetime.CancelAsync();
                    return;
                }
            }
        }

        async Task ConfirmAsync()
        {
            while (true)
            {
                await Task.Delay(ObservationPolicy.ConfirmationInterval, clock, lifetime.Token);
                hints.Writer.TryWrite(true);
            }
        }
    }

    private abstract record SourceState(
        long Version
    );

    private sealed record Disconnected(
        long Version
    ) : SourceState(Version);

    private sealed record Connected(
        ObservationCut Cut,
        CancellationTokenSource Lifetime
    ) : SourceState(Cut.SourceVersion);
}
