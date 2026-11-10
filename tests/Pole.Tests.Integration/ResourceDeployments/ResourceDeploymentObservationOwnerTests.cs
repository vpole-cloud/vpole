using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Observation;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentObservationOwnerTests
{
    [Fact]
    public async Task SourceLossFencesBeforeSlowSampleFinishesAndCannotCommitItsLateEvidence()
    {
        var store = new ObservationTestStore();
        var source = new ControlledSource();
        var sample = new TaskCompletionSource<ResourceDeploymentBoundObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new ControlledObserver((_, _) =>
        {
            entered.TrySetResult();
            return sample.Task;
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var budget = new ObservationReadBudget(TimeProvider.System);
        var run = new ResourceDeploymentObservationOwner(store, source, observer, budget, TimeProvider.System).RunAsync(
            Context(),
            Owner(),
            stop.Token);
        source.Send(ResourceDeploymentObservationChangeKind.SourceReady, "controller-a");
        await entered.Task.WaitAsync(stop.Token);
        source.Send(ResourceDeploymentObservationChangeKind.Unavailable);
        await store.Withdrawn.Task.WaitAsync(stop.Token);
        Assert.False(sample.Task.IsCompleted);
        Assert.False(store.Healthy);
        sample.SetResult(
            new ResourceDeploymentBoundObservation("controller-a", new UnavailableWorkloadObservation("Pending")));
        await Task.Delay(100, stop.Token);
        Assert.Equal(0, store.Snapshots);
        await stop.CancelAsync();
        await IgnoreCancellation(run);
    }

    [Fact]
    public async Task BurstWhileSamplingProducesOneTrailingReadAndNeverOverlapsReads()
    {
        var store = new ObservationTestStore();
        var source = new ControlledSource();
        var first = new TaskCompletionSource<ResourceDeploymentBoundObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var active = 0;
        var maximum = 0;
        var observer = new ControlledObserver(async (binding, _) =>
        {
            var running = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, running);
            try
            {
                if (Interlocked.Increment(ref count) == 1)
                {
                    entered.TrySetResult();
                    return await first.Task;
                }

                return new ResourceDeploymentBoundObservation(binding, new UnavailableWorkloadObservation("Pending"));
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var budget = new ObservationReadBudget(TimeProvider.System);
        var run = new ResourceDeploymentObservationOwner(store, source, observer, budget, TimeProvider.System).RunAsync(
            Context(),
            Owner(),
            stop.Token);
        source.Send(ResourceDeploymentObservationChangeKind.SourceReady, "controller-a");
        await entered.Task.WaitAsync(stop.Token);
        for (var i = 0; i < 200; i++)
            source.Send(ResourceDeploymentObservationChangeKind.Invalidated);
        await source.DrainAsync(stop.Token);
        Assert.False(first.Task.IsCompleted);
        first.SetResult(
            new ResourceDeploymentBoundObservation("controller-a", new UnavailableWorkloadObservation("Pending")));
        await store.TwoSnapshots.Task.WaitAsync(stop.Token);
        await Task.Delay(350, stop.Token);
        Assert.Equal(2, count);
        Assert.Equal(2, store.Snapshots);
        Assert.Equal(1, maximum);
        await stop.CancelAsync();
        await IgnoreCancellation(run);
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static ObservationOwnership Owner() => new(
        new ObservationLease("aaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbb", "replica", "token", 1),
        0);

    private static ResourceDeploymentObservationContext Context() => new(
        new ResourceDeploymentReconciliationSnapshot(
            "bbbbbbbbbbbbbbbbbbbbbbbb",
            "aaaaaaaaaaaaaaaaaaaaaaaa",
            ResourceTypeId.Create("pole/test"),
            "cccccccccccccccccccccccc",
            "dddddddddddddddddddddddd",
            "eeeeeeeeeeeeeeeeeeeeeeee",
            "ffffffffffffffffffffffff",
            1,
            1,
            1,
            DateTime.UtcNow,
            new ResourceDeploymentResolvedPlacementSnapshot(
                "aaaaaaaaaaaaaaaaaaaaaaab",
                1,
                "owned",
                null,
                null,
                new ResourceDeploymentComputeConfiguration(
                    "cccccccccccccccccccccccd",
                    1,
                    "aaaaaaaaaaaaaaaaaaaaaaab",
                    "Test",
                    new ResourceDeploymentComputeResourceValues(250, 256),
                    new ResourceDeploymentComputeResourceValues(1000, 1024),
                    ComputeArchitecture.Any,
                    [],
                    [],
                    [],
                    [],
                    [],
                    null))),
        1,
        1);

    private sealed class ControlledObserver(
        Func<string, CancellationToken, Task<ResourceDeploymentBoundObservation>> read
    ) : IResourceDeploymentBoundObserver
    {
        public Task<ResourceDeploymentBoundObservation> ObserveBoundAsync(
            ResourceDeploymentObservationContext context,
            string bindingId,
            CancellationToken cancellationToken = default) => read(bindingId, cancellationToken);
    }

    private sealed class ControlledSource : IResourceDeploymentObservationChangeSource
    {
        private readonly Channel<(ResourceDeploymentObservationChange? Change, TaskCompletionSource? Barrier)> channel
            = Channel.CreateUnbounded<(ResourceDeploymentObservationChange?, TaskCompletionSource?)>();

        public async IAsyncEnumerable<ResourceDeploymentObservationChange> WatchAsync(
            ResourceDeploymentObservationContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var (change, barrier) in channel.Reader.ReadAllAsync(cancellationToken))
                if (change is not null)
                    yield return change;
                else
                    barrier!.TrySetResult();
        }

        public void Send(ResourceDeploymentObservationChangeKind kind, string? binding = null) =>
            channel.Writer.TryWrite((new ResourceDeploymentObservationChange(kind, binding), null));

        public async Task DrainAsync(CancellationToken ct)
        {
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await channel.Writer.WriteAsync((null, barrier), ct);
            await barrier.Task.WaitAsync(ct);
        }
    }
}

internal sealed class ObservationTestStore : IResourceDeploymentObservationStore
{
    public long Version { get; set; }

    public bool Healthy { get; private set; }

    public int Snapshots { get; private set; }

    public TaskCompletionSource Withdrawn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource TwoSnapshots { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ObservationControlState?>
        LoadControlAsync(ObservationIdentity identity, CancellationToken ct = default) =>
        Task.FromResult<ObservationControlState?>(
            new ObservationControlState(1, Version, Version, Healthy, null, null));

    public Task<ObservationSourceStateOutcome> SetSourceStateAsync(
        ObservationLease lease,
        ResourceDeploymentObservationContext context,
        long expectedSourceControlVersion,
        bool healthy,
        string? bindingId,
        CancellationToken ct = default)
    {
        Healthy = healthy;
        Version++;
        if (!healthy)
            Withdrawn.TrySetResult();
        return Task.FromResult<ObservationSourceStateOutcome>(new ObservationSourceState(Version));
    }

    public Task<ObservationSnapshotOutcome> TryCommitSnapshotAsync(
        ObservationLease lease,
        ResourceDeploymentObservationContext context,
        long sourceControlVersion,
        string bindingId,
        ResourceDeploymentBoundObservation observation,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Healthy || sourceControlVersion != Version)
            return Task.FromResult<ObservationSnapshotOutcome>(
                new ObservationStoreRejected(ObservationStoreRejection.BindingChanged));
        Snapshots++;
        if (Snapshots >= 2)
            TwoSnapshots.TrySetResult();
        return Task.FromResult<ObservationSnapshotOutcome>(
            new ResourceDeploymentSharedObservation(
                context,
                bindingId,
                Version,
                Snapshots,
                DateTime.UtcNow,
                observation.Observation,
                observation.Diagnostics));
    }

    public Task<ResourceDeploymentSharedObservation?> LoadFreshSnapshotAsync(
        ResourceDeploymentObservationContext context,
        CancellationToken ct = default) => Task.FromResult<ResourceDeploymentSharedObservation?>(null);

    public Task<ObservationMutationOutcome> RenewLeaseAsync(
        ObservationLease lease,
        ResourceDeploymentObservationContext context,
        CancellationToken ct = default) =>
        Task.FromResult<ObservationMutationOutcome>(new ObservationMutationAccepted());

    public Task<ObservationMutationOutcome> PutDemandAsync(
        ObservationDemand demand,
        ResourceDeploymentObservationContext context,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ObservationMutationOutcome> RenewDemandAsync(
        ObservationDemand demand,
        ResourceDeploymentObservationContext context,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ObservationMutationOutcome> RemoveDemandAsync(
        ObservationDemandIdentity identity,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ObservationDemandPage> ScanDemandsAsync(
        string? after,
        int limit,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task ReapExpiredDemandsAsync(int limit, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ObservationLeaseOutcome> ClaimLeaseAsync(
        ResourceDeploymentObservationContext context,
        string instanceId,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ObservationMutationOutcome> ReleaseLeaseAsync(ObservationLease lease, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
