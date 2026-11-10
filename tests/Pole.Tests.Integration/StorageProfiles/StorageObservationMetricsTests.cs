using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain.Observation;
using Pole.Observation;
using Pole.Testing.Kubernetes;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.StorageProfiles;

[Collection(IntegrationTestCollection.Global3)]
public sealed class StorageObservationReadMetricsTests(
    MongoReplicaSetFixture fixture
) : IsolatedApiIntegrationTestBase(fixture, IntegrationSchemaProfile.StorageObservation)
{
    private readonly StorageNativeApi api = new(false);
    private readonly Meter meter = new(StorageObservationMetrics.MeterName);
    private readonly StorageObservationOptions options = new() { Enabled = false };
    private StorageMetricCapture capture = null!;
    private IKubernetes client = null!;
    private bool enabled = true;
    private StorageObservationMetrics metrics = null!;

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        capture = new StorageMetricCapture(meter);
        metrics = new StorageObservationMetrics(meter);
        client = NativeWatchTestClient.Create(api.Handler);
        services.RemoveAll<StorageObservationMetrics>();
        services.AddSingleton(metrics);
        services.RemoveAll<StorageObservationOptions>();
        services.AddSingleton(options);
        foreach (var registration in services.Where(item =>
                         item.ServiceType == typeof(IHostedService) &&
                         item.ImplementationType == typeof(StorageObservationCoordinator))
                     .ToArray())
            services.Remove(registration);
        services.RemoveAll<LookupDeploymentTarget>();
        services.AddSingleton<LookupDeploymentTarget>((id, _) =>
            Task.FromResult<LookupDeploymentTargetOutcome>(StorageMetricFixtures.Target(id, enabled)));
        services.RemoveAll<IKubernetesClientProvider>();
        services.AddSingleton<IKubernetesClientProvider>(new FixedKubernetesClientProvider(client));
        // The shared API fixture replaces legacy discovery delegates; this proof uses their owning adapters.
        services.RemoveAll<DiscoverKubernetesStorageClasses>();
        services.AddScoped<DiscoverKubernetesStorageClasses>(provider =>
            provider.GetRequiredService<KubernetesDiscoverStorageClasses>().InvokeAsync);
        services.RemoveAll<DiscoverKubernetesPersistentVolumes>();
        services.AddScoped<DiscoverKubernetesPersistentVolumes>(provider =>
            provider.GetRequiredService<KubernetesDiscoverPersistentVolumes>().InvokeAsync);
        services.RemoveAll<DiscoverKubernetesPersistentVolumeClaims>();
        services.AddScoped<DiscoverKubernetesPersistentVolumeClaims>(provider =>
            provider.GetRequiredService<KubernetesDiscoverPersistentVolumeClaims>().InvokeAsync);
    }

    [Fact]
    public async Task PublicAdminReadsMeasureSharedLiveAndUnavailableForOnlyFourFixedKinds()
    {
        options.Enabled = true;
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var paths = new[]
        {
            ("observation", "coherent"), ("storage-classes", "classes"), ("persistent-volumes", "volumes"),
            ("persistent-volume-claims", "claims"),
        };
        foreach (var (path, kind) in paths)
        {
            Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync(Path(path))).StatusCode);
            Assert.Equal(1, capture.Count("pole.storage.observation.reads", "live", kind));
        }

        Assert.Equal(6, api.Requests.Count);
        var context = await StorageMetricFixtures.SeedAsync(Fixture);
        var store = StorageMetricFixtures.Store(Fixture);
        var ownership = Assert.IsType<StorageObservationOwnership>(
            (await store.ClaimLeaseAsync(context, "private-replica-native-canary")).Value);
        var source = Assert.IsType<StorageObservationSourceState>(
            (await store.SetSourceStateAsync(
                ownership.Lease,
                context,
                ownership.SourceControlVersion,
                true,
                "private-source-native-canary")).Value);
        Assert.IsType<StorageObservationSnapshot>(
            (await store.TryCommitSnapshotAsync(
                ownership.Lease,
                context,
                source.SourceControlVersion,
                "private-source-native-canary",
                StorageMetricFixtures.Inventory)).Value);
        foreach (var (path, kind) in paths)
        {
            Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync(Path(path))).StatusCode);
            Assert.Equal(1, capture.Count("pole.storage.observation.reads", "shared", kind));
        }

        Assert.Equal(6, api.Requests.Count);
        enabled = false;
        foreach (var (path, kind) in paths)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Client.GetAsync(Path(path))).StatusCode);
            Assert.Equal(1, capture.Count("pole.storage.observation.reads", "unavailable", kind));
        }

        Assert.Equal(12, capture.Measurements.Count);
        capture.AssertOnlyFixedTags();
    }

    private static string Path(string path) =>
        $"/api/v1/platform/storage/{path}?deploymentTargetId={TestData.DefaultDeploymentTargetId}";

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        client.Dispose();
        api.Dispose();
        metrics.Dispose();
        capture.Dispose();
        meter.Dispose();
    }
}

[Collection(IntegrationTestCollection.Global2)]
public sealed class StorageObservationOwnerMetricsTests(
    MongoReplicaSetFixture fixture
) : MongoIntegrationTestBase(fixture)
{
    public override Task InitializeAsync() => base.InitializeAsync();
    public override Task DisposeAsync() => base.DisposeAsync();

    [Theory]
    [InlineData("accepted")]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("native_unavailable")]
    public async Task SampleOutcomeAndDurationFollowActualCommitRejectionCancellationOrRollback(string mode)
    {
        using var meter = new Meter(StorageObservationMetrics.MeterName);
        using var capture = new StorageMetricCapture(meter);
        using var metrics = new StorageObservationMetrics(meter);
        var context = await StorageMetricFixtures.SeedAsync(Fixture);
        var failSnapshot = false;
        var blockSnapshot = false;
        var commitEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = StorageMetricFixtures.Store(
            Fixture,
            async (
                session, writes,
                ct) =>
            {
                await Fixture.StoreOutboxWrites(session, writes, ct);
                if (failSnapshot)
                    throw new InvalidOperationException("private-native-canary persistence failure");
                if (blockSnapshot)
                {
                    commitEntered.TrySetResult();
                    await commitRelease.Task.WaitAsync(ct);
                }
            });
        var ownership = Assert.IsType<StorageObservationOwnership>(
            (await store.ClaimLeaseAsync(context, "private-replica-native-canary")).Value);
        var source = new StorageMetricSource();
        using var handler = new StorageMetricHandler { NativeUnavailable = mode == "native_unavailable" };
        using var client = NativeWatchTestClient.Create(handler);
        using var budget = new ObservationReadBudget(TimeProvider.System);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = new StorageObservationOwner(
            store,
            source,
            StorageMetricFixtures.Native(client, budget),
            TimeProvider.System,
            metrics).RunAsync(context, ownership, lifetime.Token);
        try
        {
            source.Send(
                new StorageObservationChange(
                    StorageObservationChangeKind.Ready,
                    "private-source-native-canary",
                    StorageMetricFixtures.Inventory));
            await handler.Entered.Task.WaitAsync(lifetime.Token);
            Assert.Equal(1, capture.Count("pole.storage.observation.sources.transitions", "gained"));
            Assert.DoesNotContain(capture.Measurements, item => item.Name == "pole.storage.observation.samples");
            if (mode == "rejected")
                await Fixture.Database
                    .GetCollection<BsonDocument>(MongoStorageObservationStore.DemandsCollection)
                    .UpdateManyAsync(
                        FilterDefinition<BsonDocument>.Empty,
                        Builders<BsonDocument>.Update.Set("ExpiresAtUtc", DateTime.UnixEpoch));
            if (mode == "failed")
                failSnapshot = true;
            if (mode == "accepted")
                blockSnapshot = true;
            if (mode == "cancelled")
                source.Send(new StorageObservationChange(StorageObservationChangeKind.Unavailable));
            else
                handler.Release.TrySetResult();
            if (mode == "accepted")
            {
                await commitEntered.Task.WaitAsync(lifetime.Token);
                Assert.Equal(0, capture.Count("pole.storage.observation.samples", "accepted"));
                Assert.Equal(
                    0,
                    await Fixture.Database
                        .GetCollection<BsonDocument>(MongoStorageObservationStore.SnapshotsCollection)
                        .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
                commitRelease.TrySetResult();
            }

            var outcome = mode == "native_unavailable" ? "rejected" : mode;
            await capture.WaitForAsync("pole.storage.observation.sample.duration", outcome, lifetime.Token);
            Assert.Equal(1, capture.Count("pole.storage.observation.samples", outcome));
            Assert.Equal(1, capture.Count("pole.storage.observation.sample.duration", outcome));
            Assert.All(
                capture.Measurements.Where(item => item.Name == "pole.storage.observation.sample.duration"),
                item => Assert.True(double.IsFinite(item.Value) && item.Value >= 0));
            Assert.Equal(
                mode == "accepted" ? 1 : 0,
                await Fixture.Database
                    .GetCollection<BsonDocument>(MongoStorageObservationStore.SnapshotsCollection)
                    .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
            if (mode == "failed")
                Assert.Equal(
                    "private-native-canary persistence failure",
                    (await Assert.ThrowsAsync<InvalidOperationException>(() => running)).Message);
            if (mode is "native_unavailable" or "cancelled")
            {
                Assert.False((await store.LoadControlAsync(context.DeploymentTargetId))!.SourceHealthy);
                Assert.Equal(1, capture.Count("pole.storage.observation.sources.transitions", "lost"));
            }

            capture.AssertOnlyFixedTags();
        }
        finally
        {
            commitRelease.TrySetResult();
            handler.Release.TrySetResult();
            lifetime.Cancel();
            try
            {
                await running;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (InvalidOperationException) when (mode == "failed")
            {
            }
        }
    }

    [Fact]
    public async Task DuplicateReadyAndUnavailableCallbacksCountOnlyCommittedBooleanTransitions()
    {
        using var meter = new Meter(StorageObservationMetrics.MeterName);
        using var capture = new StorageMetricCapture(meter);
        using var metrics = new StorageObservationMetrics(meter);
        var context = await StorageMetricFixtures.SeedAsync(Fixture);
        var store = StorageMetricFixtures.Store(Fixture);
        var ownership = Assert.IsType<StorageObservationOwnership>(
            (await store.ClaimLeaseAsync(context, "private-replica-native-canary")).Value);
        var source = new StorageMetricSource();
        using var handler = new StorageMetricHandler();
        handler.Release.TrySetResult();
        using var client = NativeWatchTestClient.Create(handler);
        using var budget = new ObservationReadBudget(TimeProvider.System);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = new StorageObservationOwner(
            store,
            source,
            StorageMetricFixtures.Native(client, budget),
            TimeProvider.System,
            metrics).RunAsync(context, ownership, lifetime.Token);
        try
        {
            var ready = new StorageObservationChange(
                StorageObservationChangeKind.Ready,
                "private-source-native-canary",
                StorageMetricFixtures.Inventory);
            source.Send(ready);
            await capture.WaitForAsync("pole.storage.observation.samples", "accepted", lifetime.Token);
            source.Send(ready);
            source.Send(ready);
            source.Send(new StorageObservationChange(StorageObservationChangeKind.Unavailable));
            source.Send(new StorageObservationChange(StorageObservationChangeKind.Unavailable));
            source.Send(ready);
            while (capture.Count("pole.storage.observation.sources.transitions", "gained") < 2)
                await Task.Delay(10, lifetime.Token);
            Assert.Equal(2, capture.Count("pole.storage.observation.sources.transitions", "gained"));
            Assert.Equal(1, capture.Count("pole.storage.observation.sources.transitions", "lost"));
            Assert.Equal(4, (await store.LoadControlAsync(context.DeploymentTargetId))!.SourceControlVersion);
            capture.AssertOnlyFixedTags();
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await running;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }
}

public sealed class StorageObservationMetricLabelTests
{
    [Fact]
    public void UnknownEnumsNormalizeAndListenerIgnoresOtherInstancesOfTheSameMeterName()
    {
        using var meter = new Meter(StorageObservationMetrics.MeterName);
        using var capture = new StorageMetricCapture(meter);
        using var metrics = new StorageObservationMetrics(meter);
        using var unrelatedMeter = new Meter(StorageObservationMetrics.MeterName);
        using var unrelated = new StorageObservationMetrics(unrelatedMeter);
        unrelated.RecordRead(StorageObservationReadOutcome.Live, StorageObservationReadKind.Claims);
        Assert.Empty(capture.Measurements);
        metrics.RecordRead((StorageObservationReadOutcome)int.MaxValue, (StorageObservationReadKind)int.MaxValue);
        metrics.RecordSample((StorageObservationSampleOutcome)int.MaxValue, TimeSpan.FromSeconds(1));
        metrics.RecordSource(true);
        Assert.Equal(1, capture.Count("pole.storage.observation.reads", "unavailable", "coherent"));
        Assert.Equal(1, capture.Count("pole.storage.observation.samples", "failed"));
        Assert.Equal(4, capture.Measurements.Count);
        capture.AssertOnlyFixedTags();
    }
}

internal static class StorageMetricFixtures
{
    internal static StorageInventoryObservation Inventory =>
        new(
            [new StorageClassObservation("class-native-canary", "driver.example", false, null, null, null)],
            [],
            [],
            true);

    internal static DeploymentTargetLookupDto Target(string id, bool enabled = true) => new(
        id,
        "private-target-native-canary",
        "private-cluster-native-canary",
        "InCluster",
        1,
        new DeploymentTargetWorkloadConfigurationLookupDto(
            1,
            null,
            new DeploymentTargetNetworkSourcesDto(ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)),
        1,
        enabled,
        false,
        false,
        "Healthy",
        TechnicalName: "test-target",
        NetworkPolicy: DeploymentTargetNetworkPolicyDto.Unknown);

    internal static MongoStorageObservationStore Store(TestDatabaseScope Fixture, StoreOutboxWrites? writes = null) =>
        new(
            Fixture.Database.GetCollection<StorageObservationControlDocument>(
                MongoStorageObservationStore.ControlsCollection),
            Fixture.Database.GetCollection<StorageObservationDemandDocument>(
                MongoStorageObservationStore.DemandsCollection),
            Fixture.Database.GetCollection<StorageObservationSnapshotDocument>(
                MongoStorageObservationStore.SnapshotsCollection),
            new MongoRunTransaction(Fixture.Client),
            (
                    request, session,
                    ct) =>
                new MongoFenceDeploymentTargetObservation(Fixture.DbContext.DeploymentTargets).InvokeAsync(
                    request,
                    session,
                    ct),
            writes ?? Fixture.StoreOutboxWrites,
            new StorageObservationOptions());

    internal static StorageObservationNativeReader Native(IKubernetes client, ObservationReadBudget budget) => new(
        new FixedKubernetesClientProvider(client),
        new StorageObservationContextLoader((id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(Target(id))),
        budget,
        new StorageObservationOptions());

    internal static async Task<StorageObservationContext> SeedAsync(TestDatabaseScope Fixture)
    {
        var id = TestData.DefaultDeploymentTargetId;
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(TestData.SeedDeploymentTarget(id));
        var context = new StorageObservationContext(id, 1);
        await Store(Fixture)
            .PutDemandAsync(
                new StorageObservationDemand(
                    new StorageObservationDemandIdentity(
                        id,
                        "private-subscription-native-canary",
                        "private-connection-native-canary",
                        "private-session-native-canary"),
                    DateTimeOffset.UtcNow.AddMinutes(20)),
                context);
        return context;
    }
}

internal sealed class StorageMetricSource : IStorageObservationSource
{
    private readonly Channel<StorageObservationChange> changes = Channel.CreateUnbounded<StorageObservationChange>();

    public async IAsyncEnumerable<StorageObservationChange> WatchAsync(
        StorageObservationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken))
            yield return change;
    }

    internal void Send(StorageObservationChange change) => changes.Writer.TryWrite(change);
}

internal sealed class StorageMetricHandler : DelegatingHandler
{
    private int reads;

    public bool NativeUnavailable { get; init; }

    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref reads) == 1)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }

        if (NativeUnavailable)
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                RequestMessage = request,
                Content = new StringContent("private-native-canary error"),
            };
        var kind = request.RequestUri!.AbsolutePath.EndsWith("storageclasses", StringComparison.Ordinal)
            ?
            "StorageClassList"
            : request.RequestUri.AbsolutePath.EndsWith("persistentvolumes", StringComparison.Ordinal)
                ? "PersistentVolumeList"
                : "PersistentVolumeClaimList";
        var apiVersion = kind == "StorageClassList" ? "storage.k8s.io/v1" : "v1";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(
                $$"""{"apiVersion":"{{apiVersion}}","kind":"{{kind}}","metadata":{"resourceVersion":"private-version-native-canary"},"items":[]} """),
        };
    }
}

internal sealed record StorageMetricMeasurement(
    string Name,
    double Value,
    KeyValuePair<string, object?>[] Tags
);

internal sealed class StorageMetricCapture : IDisposable
{
    private readonly MeterListener listener = new();

    internal StorageMetricCapture(Meter meter)
    {
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((
            instrument, value,
            tags, _) => Measurements.Enqueue(new StorageMetricMeasurement(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((
            instrument, value,
            tags, _) => Measurements.Enqueue(new StorageMetricMeasurement(instrument.Name, value, tags.ToArray())));
        listener.Start();
    }

    internal ConcurrentQueue<StorageMetricMeasurement> Measurements { get; } = new();

    public void Dispose() => listener.Dispose();

    internal int Count(
        string name,
        string outcome,
        string? kind = null) => Measurements.Count(item =>
        item.Name == name && item.Tags.Any(tag => tag.Key == "outcome" && tag.Value?.ToString() == outcome) &&
        (kind is null || item.Tags.Any(tag => tag.Key == "kind" && tag.Value?.ToString() == kind)));

    internal async Task WaitForAsync(
        string name,
        string outcome,
        CancellationToken ct)
    {
        while (Count(name, outcome) == 0)
            await Task.Delay(10, ct);
    }

    internal void AssertOnlyFixedTags()
    {
        Assert.All(
            Measurements,
            item =>
            {
                Assert.Contains(
                    item.Name,
                    new[]
                    {
                        "pole.storage.observation.reads", "pole.storage.observation.sources.transitions",
                        "pole.storage.observation.samples", "pole.storage.observation.sample.duration",
                    });
                Assert.Equal(item.Name == "pole.storage.observation.reads" ? 2 : 1, item.Tags.Length);
                Assert.All(
                    item.Tags,
                    tag =>
                    {
                        Assert.Contains(tag.Key, new[] { "outcome", "kind" });
                        Assert.Contains(
                            Assert.IsType<string>(tag.Value),
                            tag.Key == "kind"
                                ? ["coherent", "classes", "volumes", "claims"]
                                : new[]
                                {
                                    "shared", "live", "unavailable", "gained", "lost", "accepted", "rejected",
                                    "cancelled",
                                    "failed",
                                });
                        Assert.DoesNotContain("native-canary", tag.Value!.ToString());
                        Assert.DoesNotContain(TestData.DefaultDeploymentTargetId, tag.Value.ToString());
                    });
            });
    }
}
