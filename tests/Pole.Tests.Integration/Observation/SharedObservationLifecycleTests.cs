using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Pole.Observation;
using Pole.Testing.Kubernetes;
using Xunit;

namespace Pole.Tests.Integration.Observation;

public sealed class SharedObservationLifecycleTests
{
    [Fact]
    public async Task SharedReadBudgetBoundsAllCallersAndCancelsQueuedReadsAtDeadline()
    {
        var clock = new NativeWatchTestClock();
        using var budget = new ObservationReadBudget(clock);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var reads = Enumerable.Range(0, 4)
            .Select(_ => budget.ReadAsync(async ct =>
            {
                Interlocked.Increment(ref started);
                return await release.Task.WaitAsync(ct);
            }))
            .ToArray();
        Assert.Equal(4, started);
        var queued = budget.ReadAsync(_ =>
        {
            Interlocked.Increment(ref started);
            return Task.FromResult(42);
        });
        Assert.Equal(4, started);
        clock.Advance(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        foreach (var read in reads)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(4, started);
        Assert.Equal(42, await budget.ReadAsync(_ => Task.FromResult(42)));
    }

    [Fact]
    public async Task Read_budget_rejects_noncooperating_native_result_after_its_deadline()
    {
        var clock = new NativeWatchTestClock();
        using var budget = new ObservationReadBudget(clock);
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = budget.ReadAsync(_ => result.Task);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.False(read.IsCompleted);
        result.SetResult(42);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(43, await budget.ReadAsync(_ => Task.FromResult(43)));
    }

    [Fact]
    public async Task OldNoncooperatingSampleCannotWithdrawRecoveredSource()
    {
        var session = new Session();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = new ObservationOwner(session, TimeProvider.System).RunAsync(0, stop.Token);
        try
        {
            session.Changes.Writer.TryWrite(new ObservationSourceReady("old"));
            await session.Entered.Task.WaitAsync(stop.Token);
            session.Changes.Writer.TryWrite(new ObservationSourceLost());
            await session.Withdrawn.Task.WaitAsync(stop.Token);
            session.Changes.Writer.TryWrite(new ObservationSourceReady("new"));
            await session.Recovered.Task.WaitAsync(stop.Token);
            session.First.TrySetResult(ObservationSampleResult.Unavailable);
            await session.Trailing.Task.WaitAsync(stop.Token);
            Assert.Equal("new", session.SourceId);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            session.First.TrySetResult(ObservationSampleResult.Unavailable);
            await stop.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }
    }

    private sealed class Session : IObservationSession
    {
        private int reads;
        private long version;
        internal string? SourceId { get; private set; }
        internal Channel<ObservationChange> Changes { get; } = Channel.CreateUnbounded<ObservationChange>();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Withdrawn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Trailing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<ObservationSampleResult> First { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ObservationChange> WatchAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var change in Changes.Reader.ReadAllAsync(ct))
                yield return change;
        }

        public Task<long?> SetSourceAsync(
            long expected,
            string? sourceId,
            CancellationToken ct)
        {
            Assert.Equal(version, expected);
            SourceId = sourceId;
            version++;
            if (sourceId is null)
                Withdrawn.TrySetResult();
            if (sourceId == "new")
                Recovered.TrySetResult();
            return Task.FromResult<long?>(version);
        }

        public async Task<ObservationSampleResult> SampleAsync(ObservationCut cut, CancellationToken ct)
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                Entered.TrySetResult();
                return await First.Task; // Deliberately ignores cancellation, like a late native reply.
            }

            Assert.Equal("new", cut.SourceId);
            Trailing.TrySetResult();
            return ObservationSampleResult.Accepted;
        }

        public Task<bool> RenewAsync(CancellationToken ct) => Task.FromResult(true);

        public void RecordSource(bool healthy)
        {
        }

        public void RecordSample(ObservationSampleMetric outcome, TimeSpan elapsed)
        {
        }
    }
}
