using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Pole.Observation;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class SharedObservationOwnerTests
{
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
        private long version;
        private int reads;
        internal string? SourceId { get; private set; }
        internal Channel<ObservationChange> Changes { get; } = Channel.CreateUnbounded<ObservationChange>();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Withdrawn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Trailing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<ObservationSampleResult> First { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ObservationChange> WatchAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var change in Changes.Reader.ReadAllAsync(ct))
                yield return change;
        }

        public Task<long?> SetSourceAsync(long expected, string? sourceId, CancellationToken ct)
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
