using System.Collections.Immutable;
using Pole.Observation;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using k8s;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Tests.Integration.ResourceDeployments;
using Pole.Workloads;
using Pole.Testing.Kubernetes;
using Xunit;

namespace Pole.Tests.Integration.Observability;

public sealed class ObservationMetricsTests
{
    [Fact]
    public async Task ObservationReadsAndOwnerTransitionsMeasureOnlyAggregateOutcomes()
    {
        using var meter = new Meter(ResourceDeploymentObservationMetrics.MeterName);
        using var capture = new Capture(meter);
        using var metrics = new ResourceDeploymentObservationMetrics(meter);
        var deployment = ResourceDeploymentFactory.Create("aaaaaaaaaaaaaaaaaaaaaaaa",
            ResourceTypeId.Create("pole/test"),
            "cccccccccccccccccccccccc",
            "dddddddddddddddddddddddd",
            "eeeeeeeeeeeeeeeeeeeeeeee",
            ResolvedResourcePlacement.Create("ffffffffffffffffffffffff",
                "aaaaaaaaaaaaaaaaaaaaaaab",
                "private-namespace",
                1,
                1,
                null,
                null,
                ResourceDeploymentTestData.Compute(),
                DateTime.UtcNow),
            ResourcePlacementCapability.SingleTarget,
            true,
            ResourceDeploymentFactory.DefaultOptions with
            {
                Id = "bbbbbbbbbbbbbbbbbbbbbbbb",
                CreatedAtUtc = DateTime.UtcNow
            });
        var context =
            new ResourceDeploymentObservationContext(deployment.ToReconciliationSnapshot(), deployment.Revision, 1);
        var store = new Store();
        var observer = new Observer();
        var source = new Source();
        var catalog = new ResourceDeploymentObserverCatalog([
            ResourceDeploymentObserverDescriptor.Create<Observer, Source>(ResourceTypeId.Create("pole/test"))
        ]);
        var target = new DeploymentTargetLookupDto("aaaaaaaaaaaaaaaaaaaaaaab",
            "private-target",
            "private-cluster",
            "Kubeconfig",
            1,
            new(1,
                null,
                new(ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)),
            1,
            true,
            true,
            false,
            "Healthy",
        TechnicalName: "test-target",
        NetworkPolicy: DeploymentTargetNetworkPolicyDto.Unknown);
        var targetAvailable = true;
        var loader = new ResourceDeploymentObservationContextLoader(null!,
            catalog,
            (_, _) => Task.FromResult<LookupDeploymentTargetOutcome>(targetAvailable
                ? target
                : new DeploymentTargetLookupError(new DeploymentTargetNotFound(target.Id))));
        var reader = new ResourceDeploymentSharedObservationReader(store, loader, new(), metrics);
        Assert.IsType<LiveObservationRead>(await reader.BeginAsync(deployment, default));
        store.Snapshot = new(context,
            "controller-private",
            1,
            1,
            DateTime.UtcNow,
            new UnavailableWorkloadObservation("private-reason"),
            null);
        Assert.IsType<SharedObservationRead>(await reader.BeginAsync(deployment, default));
        targetAvailable = false;
        Assert.IsType<UnavailableObservationRead>(await reader.BeginAsync(deployment, default));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var budget = new ObservationReadBudget(TimeProvider.System);
        var ownership =
            new ObservationOwnership(new(deployment.TenantId, deployment.Id, "replica-private", "lease-private", 1), 0);
        var running =
            new ResourceDeploymentObservationOwner(store, source, observer, budget, TimeProvider.System, metrics)
                .RunAsync(context, ownership, lifetime.Token);
        source.Send(ResourceDeploymentObservationChangeKind.SourceReady, "controller-private");
        await capture.WaitForAsync("pole.observation.samples", "accepted", lifetime.Token);
        store.RejectSample = true;
        source.Send(ResourceDeploymentObservationChangeKind.Invalidated, "controller-private");
        await capture.WaitForAsync("pole.observation.samples", "rejected", lifetime.Token);
        source.Send(ResourceDeploymentObservationChangeKind.Unavailable);
        await capture.WaitForAsync("pole.observation.sources.transitions", "lost", lifetime.Token);
        await lifetime.CancelAsync();
        try
        {
            await running;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }

        foreach (var outcome in new[] { "shared", "live", "unavailable" })
            Assert.True(capture.Has("pole.observation.reads", outcome));
        Assert.True(capture.Has("pole.observation.sources.transitions", "gained"));
        Assert.Contains(capture.Measurements,
            item => item.Name == "pole.observation.sample.duration" && item.Value >= 0);
        Assert.All(capture.Measurements.SelectMany(item => item.Tags),
            tag =>
            {
                Assert.Equal("outcome", tag.Key);
                Assert.Contains(tag.Value?.ToString(),
                    new[]
                    {
                        "shared", "live", "unavailable", "gained", "lost", "accepted", "rejected", "cancelled", "failed"
                    });
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeWatchRecoveryLabelsExcludeUrisVersionsMessagesAndObjectMetadata(bool malformed)
    {
        using var meter = new Meter(KubernetesWatchMetrics.MeterName);
        using var capture = new Capture(meter);
        using var metrics = new KubernetesWatchMetrics(meter);
        using var bad = new NativeWatchTestStream();
        using var healthy = new NativeWatchTestStream();
        var watches = 0;
        using var handler = new NativeWatchTestHandler(_ =>
                "{\"metadata\":{\"resourceVersion\":\"private-version\"},\"items\":[]}",
            _ => ++watches == 1 ? bad : healthy);
        using var client = NativeWatchTestClient.Create(handler);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = KubernetesListWatch.WatchAsync<V1Deployment>(async (_, token) =>
            {
                var listed =
                    await client.AppsV1.ListNamespacedDeploymentAsync("private-namespace", cancellationToken: token);
                return new(listed.Items.ToArray(), listed.Metadata.ResourceVersion, null);
            },
            (version, error, token) => client.AppsV1.WatchListNamespacedDeploymentAsync("private-namespace",
                resourceVersion: version,
                onError: error,
                cancellationToken: token),
            item => item.Metadata.ResourceVersion,
            TimeProvider.System,
            cancellationToken: lifetime.Token,
            metrics: metrics).GetAsyncEnumerator(lifetime.Token);
        await stream.MoveNextAsync();
        Assert.Equal(KubernetesListWatchChangeKind.Listed, stream.Current.Kind);
        await stream.MoveNextAsync();
        Assert.Equal(KubernetesListWatchChangeKind.Ready, stream.Current.Kind);
        bad.Write(malformed
            ? "{invalid-secret-message\n"
            : "{\"type\":\"ERROR\",\"object\":{\"apiVersion\":\"v1\",\"kind\":\"Status\",\"code\":410,\"message\":\"private-secret-message\"}}\n");
        await stream.MoveNextAsync().AsTask().WaitAsync(lifetime.Token);
        Assert.Equal(KubernetesListWatchChangeKind.Unavailable, stream.Current.Kind);
        await stream.MoveNextAsync().AsTask().WaitAsync(lifetime.Token);
        Assert.Equal(KubernetesListWatchChangeKind.Listed, stream.Current.Kind);
        await stream.MoveNextAsync().AsTask().WaitAsync(lifetime.Token);
        Assert.Equal(KubernetesListWatchChangeKind.Ready, stream.Current.Kind);
        healthy.Complete();
        await stream.MoveNextAsync().AsTask().WaitAsync(lifetime.Token);
        Assert.Equal(KubernetesListWatchChangeKind.Unavailable, stream.Current.Kind);
        await lifetime.CancelAsync();
        await stream.DisposeAsync();
        foreach (var recovery in new[] { "listed", "opened", malformed ? "malformed" : "410", "eof", "cancelled" })
            Assert.True(capture.Has("pole.kubernetes.watch.outcomes", recovery),
                "Missing bounded recovery category: " + recovery + "; bounded categories: " + string.Join(",",
                    capture.Measurements.SelectMany(item => item.Tags).Select(tag => tag.Value?.ToString())));
        Assert.All(capture.Measurements.SelectMany(item => item.Tags),
            tag =>
            {
                Assert.Equal("recovery", tag.Key);
                Assert.Contains(tag.Value?.ToString(),
                    new[]
 {
                        "listed", "opened", "eof", "410", "malformed", "stalled_headers", "cancelled", "unavailable"
                    });
            });
    }

    [Fact]
    public async Task StalledNativeHeadersAreMeasuredWithoutClaimingAnOpenedWatch()
    {
        using var meter = new Meter(KubernetesWatchMetrics.MeterName);
        using var capture = new Capture(meter);
        using var metrics = new KubernetesWatchMetrics(meter);
        var clock = new NativeWatchTestClock();
        using var handler = new StalledHeadersHandler();
        using var client = NativeWatchTestClient.Create(handler);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = KubernetesListWatch.WatchAsync<V1Deployment>(async (_, token) =>
            {
                var listed =
                    await client.AppsV1.ListNamespacedDeploymentAsync("private-namespace", cancellationToken: token);
                return new(listed.Items.ToArray(), listed.Metadata.ResourceVersion, null);
            },
            (version, error, token) => client.AppsV1.WatchListNamespacedDeploymentAsync("private-namespace",
                resourceVersion: version,
                onError: error,
                cancellationToken: token),
            item => item.Metadata.ResourceVersion,
            clock,
            cancellationToken: lifetime.Token,
            metrics: metrics).GetAsyncEnumerator(lifetime.Token);
        await stream.MoveNextAsync();
        var pending = stream.MoveNextAsync().AsTask();
        await handler.Entered.Task.WaitAsync(lifetime.Token);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(await pending.WaitAsync(lifetime.Token));
        Assert.Equal(KubernetesListWatchChangeKind.Unavailable, stream.Current.Kind);
        Assert.True(capture.Has("pole.kubernetes.watch.outcomes", "stalled_headers"));
        Assert.False(capture.Has("pole.kubernetes.watch.outcomes", "opened"));
        await lifetime.CancelAsync();
        await stream.DisposeAsync();
        Assert.All(capture.Measurements.SelectMany(item => item.Tags),
            tag =>
            {
                Assert.Equal("recovery", tag.Key);
                Assert.Contains(tag.Value?.ToString(), new[] { "listed", "stalled_headers", "cancelled" });
            });
    }

    private sealed class StalledHeadersHandler : DelegatingHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (NativeWatchTestClient.IsWatch(request.RequestUri!))
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(NativeWatchTestClient.DeploymentList("private-version"))
            };
        }
    }

    private sealed class Observer : IResourceDeploymentObserver, IResourceDeploymentBoundObserver
    {
        public Task<WorkloadObservation> ObserveAsync(ResourceDeploymentReconciliationSnapshot deployment,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkloadObservation>(new UnavailableWorkloadObservation("private-reason"));

        public Task<ResourceDeploymentBoundObservation> ObserveBoundAsync(ResourceDeploymentObservationContext context,
            string bindingId,
            CancellationToken cancellationToken = default) => Task.FromResult(
            new ResourceDeploymentBoundObservation(bindingId, new UnavailableWorkloadObservation("private-reason")));
    }

    private sealed class Source : IResourceDeploymentObservationChangeSource
    {
        private readonly Channel<ResourceDeploymentObservationChange> changes =
            Channel.CreateUnbounded<ResourceDeploymentObservationChange>();

        public void Send(ResourceDeploymentObservationChangeKind kind, string? binding = null) =>
            changes.Writer.TryWrite(new(kind, binding));

        public async IAsyncEnumerable<ResourceDeploymentObservationChange> WatchAsync(
            ResourceDeploymentObservationContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken))
                yield return change;
        }
    }

    private sealed class Store : IResourceDeploymentObservationStore
    {
        private readonly ObservationTestStore inner = new();

        public bool RejectSample { get; set; }

        public ResourceDeploymentSharedObservation? Snapshot { get; set; }

        public Task<ObservationControlState?> LoadControlAsync(ObservationIdentity identity,
            CancellationToken ct = default) => inner.LoadControlAsync(identity, ct);

        public Task<ResourceDeploymentSharedObservation?> LoadFreshSnapshotAsync(
            ResourceDeploymentObservationContext context,
            CancellationToken ct = default) => Task.FromResult(Snapshot);

        public Task<ObservationSourceStateOutcome> SetSourceStateAsync(ObservationLease lease,
            ResourceDeploymentObservationContext context,
            long version,
            bool healthy,
            string? binding,
            CancellationToken ct = default) => inner.SetSourceStateAsync(lease, context, version, healthy, binding, ct);

        public Task<ObservationSnapshotOutcome> TryCommitSnapshotAsync(ObservationLease lease,
            ResourceDeploymentObservationContext context,
            long version,
            string binding,
            ResourceDeploymentBoundObservation observation,
            CancellationToken ct = default) => RejectSample
            ? Task.FromResult<ObservationSnapshotOutcome>(
                new ObservationStoreRejected(ObservationStoreRejection.BindingChanged))
            : inner.TryCommitSnapshotAsync(lease, context, version, binding, observation, ct);

        public Task<ObservationMutationOutcome> RenewLeaseAsync(ObservationLease lease,
            ResourceDeploymentObservationContext context,
            CancellationToken ct = default) => inner.RenewLeaseAsync(lease, context, ct);

        public Task<ObservationMutationOutcome> PutDemandAsync(ObservationDemand demand,
            ResourceDeploymentObservationContext context,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ObservationMutationOutcome> RenewDemandAsync(ObservationDemand demand,
            ResourceDeploymentObservationContext context,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ObservationMutationOutcome> RemoveDemandAsync(ObservationDemandIdentity identity,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ObservationDemandPage> ScanDemandsAsync(string? after, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ReapExpiredDemandsAsync(int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ObservationLeaseOutcome> ClaimLeaseAsync(ResourceDeploymentObservationContext context,
            string instance,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ObservationMutationOutcome> ReleaseLeaseAsync(ObservationLease lease,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed record Measurement(
        string Name,
        double Value,
        KeyValuePair<string, object?>[] Tags);

    private sealed class Capture : IDisposable
    {
        public ConcurrentQueue<Measurement> Measurements { get; } = new();

        private readonly MeterListener listener = new();

        public Capture(Meter meter)
        {
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    owner.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Measurements.Enqueue(new(instrument.Name, value, tags.ToArray())));
            listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                Measurements.Enqueue(new(instrument.Name, value, tags.ToArray())));
            listener.Start();
        }

        public bool Has(string name, string outcome) => Measurements.Any(item =>
            item.Name == name && item.Tags.Any(tag => tag.Value?.ToString() == outcome));

        public async Task WaitForAsync(string name, string outcome, CancellationToken ct)
        {
            while (!Has(name, outcome))
                await Task.Delay(10, ct);
        }

        public void Dispose() => listener.Dispose();
    }
}
