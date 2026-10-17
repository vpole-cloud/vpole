using Pole.Observation;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text.Json;
using k8s;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Xunit;

namespace Pole.Tests.Integration.StorageProfiles;

public sealed class StorageObservationNativeTests
{
    private static readonly StorageObservationContext Context = new(TestData.DefaultDeploymentTargetId, 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllThreeQuietWatchHeadersAreRequiredAndEmptyInventoryIsHealthy(bool empty)
    {
        using var api = new StorageNativeApi(empty);
        using var client = KubernetesNativeWatchTests.Client(api.Handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var budget = new ObservationReadBudget(TimeProvider.System);
        var changes = Source(client, budget).WatchAsync(Context, cancellation.Token).GetAsyncEnumerator();
        var ready = await NextAsync(changes, StorageObservationChangeKind.Ready);
        Assert.True(ready.Inventory!.Complete);
        Assert.Equal(empty ? 0 : 1, ready.Inventory.StorageClasses.Length);
        Assert.Empty(ready.Inventory.PersistentVolumes);
        Assert.Empty(ready.Inventory.PersistentVolumeClaims);
        Assert.Equal(3, api.Requests.Count(KubernetesNativeWatchTests.IsWatch));
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(ready));
        foreach (var uri in api.Requests.Where(KubernetesNativeWatchTests.IsWatch))
        {
            var key = StorageNativeApi.Key(uri.AbsolutePath);
            Assert.Contains($"resourceVersion={key}-list", Uri.UnescapeDataString(uri.Query));
        }

        cancellation.Cancel();
        await changes.DisposeAsync();
        Assert.All(api.Streams.Values, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task MaximumQualifiedProvisionerAllowsAllThreeStreamsToBecomeReady()
    {
        using var api = new StorageNativeApi(false) { Provisioner = StorageNativeApi.MaximumQualifiedProvisioner };
        using var client = KubernetesNativeWatchTests.Client(api.Handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var budget = new ObservationReadBudget(TimeProvider.System);
        await using var changes = Source(client, budget).WatchAsync(Context, cancellation.Token).GetAsyncEnumerator();
        var ready = await NextAsync(changes, StorageObservationChangeKind.Ready);
        Assert.True(ready.Inventory!.Complete);
        Assert.Equal(317, Assert.Single(ready.Inventory.StorageClasses).Provisioner.Length);
        Assert.Equal(api.Provisioner, Assert.Single(ready.Inventory.StorageClasses).Provisioner);
        Assert.Equal(3, api.Requests.Count(KubernetesNativeWatchTests.IsWatch));
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(ready));
        cancellation.Cancel();
    }

    [Fact]
    public async Task UidReplacementFencesOldCutAndLateOldDeleteCannotRemoveReplacement()
    {
        using var api = new StorageNativeApi(false);
        using var client = KubernetesNativeWatchTests.Client(api.Handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var budget = new ObservationReadBudget(TimeProvider.System);
        var changes = Source(client, budget).WatchAsync(Context, cancellation.Token).GetAsyncEnumerator();
        var first = await NextAsync(changes, StorageObservationChangeKind.Ready);
        api.Streams["classes"].Write(StorageNativeApi.ClassFrame("ADDED", "replacement", "replace-version"));
        Assert.Equal(StorageObservationChangeKind.Unavailable,
            (await NextAsync(changes, StorageObservationChangeKind.Unavailable)).Kind);
        var replaced = await NextAsync(changes, StorageObservationChangeKind.Ready);
        Assert.NotEqual(first.SourceId, replaced.SourceId);
        Assert.Single(replaced.Inventory!.StorageClasses);
        api.Streams["classes"].Write(StorageNativeApi.ClassFrame("DELETED", "original", "old-delete-version"));
        var late = await NextAsync(changes, StorageObservationChangeKind.Changed);
        Assert.Equal(replaced.SourceId, late.SourceId);
        Assert.Single(late.Inventory!.StorageClasses);
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokenStreamImmediatelyWithdrawsCoverageAndRecoveryUsesNewSourceId(bool malformed)
    {
        using var api = new StorageNativeApi(false);
        using var client = KubernetesNativeWatchTests.Client(api.Handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var budget = new ObservationReadBudget(TimeProvider.System);
        var changes = Source(client, budget).WatchAsync(Context, cancellation.Token).GetAsyncEnumerator();
        var before = await NextAsync(changes, StorageObservationChangeKind.Ready);
        api.Streams["claims"].Write(malformed
            ? "{broken-json\n"
            : "{\"type\":\"ERROR\",\"object\":{\"apiVersion\":\"v1\",\"kind\":\"Status\",\"code\":410,\"message\":\"native-canary\"}}\n");
        await NextAsync(changes, StorageObservationChangeKind.Unavailable);
        var recovered = await NextAsync(changes, StorageObservationChangeKind.Ready);
        Assert.NotEqual(before.SourceId, recovered.SourceId);
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(recovered));
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task ItemOverflowNeverClaimsCoverageOrOpensOverflowedKindWatch()
    {
        using var api = new StorageNativeApi(false) { ExtraClass = true };
        using var client = KubernetesNativeWatchTests.Client(api.Handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var budget = new ObservationReadBudget(TimeProvider.System);
        var changes = Source(client, budget, new()
        {
            MaximumItemsPerKind = 1
        }).WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        var first = await NextAsync(changes, StorageObservationChangeKind.Unavailable);
        Assert.Null(first.Inventory);
        Assert.DoesNotContain(api.Requests,
            uri => KubernetesNativeWatchTests.IsWatch(uri) && StorageNativeApi.Key(uri.AbsolutePath) == "classes");
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    private static KubernetesStorageObservationSource Source(IKubernetes client,
        IObservationReadBudget budget,
        StorageObservationOptions? options = null)
    {
        var contexts = new StorageObservationContextLoader((id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(
            new DeploymentTargetLookupDto(id,
                "Target",
                "cluster",
                "InCluster",
                1,
                new(1, null, new(ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)),
                1,
                true,
                false,
                false,
                "Healthy")));
        var limits = options ?? new();
        return new(new StorageObservationNativeReader(new FixedKubernetesClientProvider(client),
                contexts,
                budget,
                limits),
            limits,
            TimeProvider.System);
    }

    internal static async Task<StorageObservationChange> NextAsync(IAsyncEnumerator<StorageObservationChange> changes,
        StorageObservationChangeKind kind)
    {
        for (var count = 0; count < 30; count++)
        {
            Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            if (changes.Current.Kind == kind)
                return changes.Current;
        }

        throw new InvalidOperationException("The expected storage observation change was not delivered.");
    }
}

internal sealed class StorageNativeApi : IDisposable
{
    private readonly bool empty;

    public NativeWatchTestHandler Handler
    {
        get;
    }

    public ConcurrentDictionary<string, NativeWatchTestStream> Streams
    {
        get;
    } = new(StringComparer.Ordinal);

    public ConcurrentQueue<Uri> Requests => Handler.Requests;

    public bool ExtraClass
    {
        get;
        init;
    }

    public string Provisioner
    {
        get;
        init;
    } = "driver.example";

    internal static string MaximumQualifiedProvisioner =>
        string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61)) + "/" +
        new string('e', 63);

    public StorageNativeApi(bool empty)
    {
        this.empty = empty;
        Handler = new NativeWatchTestHandler(List,
            request =>
            {
                var key = Key(request.RequestUri!.AbsolutePath);
                var stream = new NativeWatchTestStream();
                Streams[key] = stream;
                return stream;
            });
    }

    internal static string Key(string path) => path.EndsWith("storageclasses", StringComparison.Ordinal) ? "classes" :
        path.EndsWith("persistentvolumes", StringComparison.Ordinal) ? "volumes" : "claims";

    private string List(HttpRequestMessage request)
    {
        var key = Key(request.RequestUri!.AbsolutePath);
        var kind = key == "classes" ? "StorageClassList" :
            key == "volumes" ? "PersistentVolumeList" : "PersistentVolumeClaimList";
        var version = key == "classes" ? "storage.k8s.io/v1" : "v1";
        var items = key == "classes" && !empty
            ? ClassObject("original", "class-a", provisioner: Provisioner) +
              (ExtraClass ? "," + ClassObject("extra", "class-b", provisioner: Provisioner) : "")
            : "";
        return
            $$"""{"apiVersion":"{{version}}","kind":"{{kind}}","metadata":{"resourceVersion":"{{key}}-list"},"items":[{{items}}]}""";
    }

    private static string ClassObject(string uid,
        string name,
        string version = "class-version",
        string provisioner = "driver.example") => JsonSerializer.Serialize(new
        {
            apiVersion = "storage.k8s.io/v1",
            kind = "StorageClass",
            metadata = new
            {
                name,
                uid,
                resourceVersion = version,
                annotations = new Dictionary<string, string>
                {
                    ["pole.io/raw"] = "native-canary",
                    ["storageclass.kubernetes.io/is-default-class"] = "true"
                }
            },
            provisioner,
            parameters = new Dictionary<string, string> { ["raw"] = "native-canary" }
        });

    internal static string ClassFrame(string type, string uid, string version) => JsonSerializer.Serialize(new
    {
        type,
        @object = JsonSerializer.Deserialize<JsonElement>(ClassObject(uid, "class-a", version))
    }) + "\n";

    public void Dispose()
    {
        Handler.Dispose();
        foreach (var stream in Streams.Values)
            stream.Dispose();
    }
}
