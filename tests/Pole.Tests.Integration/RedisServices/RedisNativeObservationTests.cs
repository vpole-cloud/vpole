using Pole.Observation;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Tests.Integration.ResourceDeployments;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.RedisServices;

public sealed class RedisNativeObservationTests
{
    internal static readonly Redis Resource = RedisFactory.Create("507f1f77bcf86cd799439010",
        "cache",
        "507f1f77bcf86cd799439012",
        new RedisSpec(new Storage(1, "disk")),
        RedisFactory.DefaultCreateOptions with
        {
            Id = "507f1f77bcf86cd799439011"
        });

    internal static readonly ResourceDeploymentObservationContext Context = new(
        new ResourceDeploymentReconciliationSnapshot(
            "deployment",
            Resource.TenantId,
            RedisResourceDeployment.ResourceType,
            Resource.Id,
            Resource.ResourceGroupId,
            "scope",
            "cell",
            2,
            1,
            2,
            DateTime.UtcNow,
            new ResourceDeploymentResolvedPlacementSnapshot("target",
                1,
                "owned",
                null,
                null,
                ResourceDeploymentTestData.ContractCompute("target"))),
        3,
        1);

    internal static readonly WorkloadIdentityLabels Identity = new(Resource.TenantId,
        Resource.ResourceGroupId,
        "scope",
        "cell",
        "target",
        "Redis",
        Resource.Id,
        "deployment");

    [Fact]
    public async Task Quiet_owned_StatefulSet_watch_covers_all_interests_without_first_frame_or_read_budget_slot()
    {
        using var api = new RedisNativeTestApi();
        using var client = KubernetesNativeWatchTests.Client(api);
        var budget = new Budget();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client), budget).WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.Equal("controller", changes.Current.BindingId);
        Assert.Equal(6,
            api.Requests.Count(KubernetesNativeWatchTests
                .IsWatch)); // controller, Pod, Service, two UID warnings, slice
        Assert.Equal(0, budget.Active);
        Assert.True(budget.Reads >= 6);
        Assert.All(api.Streams, stream => Assert.Equal(0, stream.FramesWritten));
        foreach (var request in api.Requests.Where(KubernetesNativeWatchTests.IsWatch))
            Assert.Contains("resourceVersion=", request.Query);
        var controllerQuery = Uri.UnescapeDataString(api.Requests.First().Query);
        foreach (var key in Identity.ToDictionary().Keys)
            Assert.Contains(key, controllerQuery);
        Assert.Contains("metadata.name=cache", controllerQuery);
        cancellation.Cancel();
        await changes.DisposeAsync();
        Assert.All(api.Streams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task Empty_controller_watch_remains_open_and_added_recovers_binding()
    {
        using var api = new RedisNativeTestApi { ControllerPresent = false };
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client)).WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Equal(2, api.Requests.Count);
        api.ControllerPresent = true;
        api.Stream("statefulsets").Write(RedisNativeTestApi.Frame("ADDED", api.Controller()));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.Equal("controller", changes.Current.BindingId);
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Theory]
    [InlineData("pole.io/tenant-id")]
    [InlineData("pole.io/resource-group-id")]
    [InlineData("pole.io/isolation-scope-id")]
    [InlineData("pole.io/runtime-cell-id")]
    [InlineData("pole.io/deployment-target-id")]
    [InlineData("pole.io/resource-type")]
    [InlineData("pole.io/resource-id")]
    [InlineData("pole.io/resource-deployment-id")]
    [InlineData("namespace")]
    [InlineData("name")]
    [InlineData("uid")]
    public async Task Controller_ownership_mismatch_never_opens_dependents(string field)
    {
        using var api = new RedisNativeTestApi { ControllerMismatch = field };
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client)).WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Null(changes.Current.BindingId);
        Assert.Equal(2, api.Requests.Count);
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Selector_loss_withdraws_controller_binding_before_starting_replacement_reads()
    {
        using var api = new RedisNativeTestApi();
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client)).WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        var item = api.Controller();
        item.Metadata.Labels["pole.io/tenant-id"] = "foreign-native-canary";
        api.Stream("statefulsets").Write(RedisNativeTestApi.Frame("DELETED", item));
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(ResourceDeploymentObservationChangeKind.BindingChanged, changes.Current.Kind);
        Assert.Null(changes.Current.BindingId);
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(changes.Current));
        cancellation.Cancel();
        await changes.DisposeAsync();
        Assert.All(api.Streams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task Direct_owned_Pods_only_get_at_most_eight_warning_interests_and_parent_change_retires_warning()
    {
        using var api = new RedisNativeTestApi { PodCount = 12, IncludeForeignPod = true };
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client)).WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.Equal(8,
            api.Requests.Count(uri =>
                uri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) &&
                KubernetesNativeWatchTests.IsWatch(uri)));
        Assert.DoesNotContain(api.Requests.Where(uri => uri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal)),
            uri => uri.Query.Contains("foreign", StringComparison.Ordinal));
        var old = api.Stream("events", "pod-00");
        var removed = api.Pod("pod-00");
        removed.Metadata.OwnerReferences[0].Uid = "foreign-controller";
        api.Stream("pods").Write(RedisNativeTestApi.Frame("MODIFIED", removed));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Invalidated);
        Assert.True(old.Disposed);
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Theory]
    [InlineData("pods")]
    [InlineData("endpointslices")]
    public async Task Incremental_101st_UID_fails_closed_and_drains_every_stream(string kind)
    {
        using var api = new RedisNativeTestApi
        {
            PodCount = kind == "pods" ? 100 : 1,
            SliceCount = kind == "endpointslices" ? 100 : 1
        };
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client)).WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        api.Stream(kind).Write(kind == "pods"
            ? RedisNativeTestApi.Frame("ADDED", api.Pod("overflow"))
            : RedisNativeTestApi.Frame("ADDED", api.Slice("overflow")));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("NativeObjectLimit", failure.Message);
        Assert.All(api.Streams, stream => Assert.True(stream.Disposed));
        await changes.DisposeAsync();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("namespace")]
    [InlineData("service-label")]
    public async Task Foreign_slice_cannot_invalidate_even_without_Pole_labels(string mismatch)
    {
        using var api = new RedisNativeTestApi();
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client)).WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        var slice = api.Slice("foreign-slice");
        if (mismatch == "owner")
            slice.Metadata.OwnerReferences[0].Uid = "foreign-service";
        else if (mismatch == "namespace")
            slice.Metadata.NamespaceProperty = "foreign";
        else
            slice.Metadata.Labels["kubernetes.io/service-name"] = "foreign";
        var stream = api.Stream("endpointslices");
        stream.Write(RedisNativeTestApi.Frame("ADDED", slice));
        var next = changes.MoveNextAsync().AsTask();
        await stream.NextReadAfterFrame.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await next.WaitAsync(TimeSpan.FromSeconds(2)));
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Service_replacement_retires_old_slice_UID_interest_and_opens_new_independent_RV()
    {
        using var api = new RedisNativeTestApi();
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client)).WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        var old = api.Stream("endpointslices");
        api.ServiceUid = "replacement-service";
        api.Stream("services").Write(RedisNativeTestApi.Frame("MODIFIED", api.Service()));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.True(old.Disposed);
        Assert.NotSame(old, api.Stream("endpointslices"));
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Bound_sample_rejects_UID_replacement_before_during_or_after_sampling(int replacement)
    {
        using var api = new RedisNativeTestApi { ReplaceAtRead = replacement };
        using var client = KubernetesNativeWatchTests.Client(api);
        var result =
            await new RedisResourceDeploymentObserver((_, _, _) => Task.FromResult<Redis?>(Resource),
                new ClientProvider(() => client)).ObserveBoundAsync(Context, "controller");
        Assert.Null(result.BindingId);
        Assert.Equal("BindingChanged", Assert.IsType<UnavailableWorkloadObservation>(result.Observation).Reason);
        Assert.IsType<UnavailableWorkloadDiagnostics>(result.Diagnostics);
    }

    [Fact]
    public async Task Same_UID_bound_sample_returns_sanitized_readiness_and_runtime_instances()
    {
        using var api = new RedisNativeTestApi();
        using var client = KubernetesNativeWatchTests.Client(api);
        var result =
            await new RedisResourceDeploymentObserver((_, _, _) => Task.FromResult<Redis?>(Resource),
                new ClientProvider(() => client)).ObserveBoundAsync(Context, "controller");
        Assert.Equal("controller", result.BindingId);
        Assert.True(Assert.IsType<ObservedWorkload>(result.Observation).Ready);
        Assert.Equal("pod-00",
            Assert.Single(Assert.IsType<AvailableWorkloadDiagnostics>(result.Diagnostics).Instances).Uid);
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(result));
        Assert.Equal(4, api.ControllerReads);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Disabled_or_deleting_target_cannot_open_any_native_request(bool enabled, bool deleting)
    {
        using var api = new RedisNativeTestApi();
        using var client = KubernetesNativeWatchTests.Client(api);
        var provider = new ClientProvider(() => client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(provider, target: () => Target(1) with { Enabled = enabled, DeletionRequested = deleting })
            .WatchAsync(Context, cancellation.Token).GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Equal(0, provider.Reads);
        Assert.Empty(api.Requests);
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Rotation_fences_retry_and_new_context_reacquires_client_without_disposing_old_cached_client()
    {
        using var firstApi = new RedisNativeTestApi();
        using var secondApi = new RedisNativeTestApi { ControllerUid = "replacement" };
        using var firstClient = KubernetesNativeWatchTests.Client(firstApi);
        using var secondClient = KubernetesNativeWatchTests.Client(secondApi);
        var version = 1L;
        var provider = new ClientProvider(() => version == 1 ? firstClient : secondClient);
        var source = Source(provider, target: () => Target(version));
        using var cancelFirst = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = source.WatchAsync(Context, cancelFirst.Token).GetAsyncEnumerator();
        await NextAsync(first, ResourceDeploymentObservationChangeKind.SourceReady);
        var reads = provider.Reads;
        version = 2;
        firstApi.Stream("statefulsets").Complete();
        await NextAsync(first, ResourceDeploymentObservationChangeKind.Unavailable);
        await NextAsync(first, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Equal(reads, provider.Reads);
        cancelFirst.Cancel();
        await first.DisposeAsync();
        await firstClient.AppsV1.ListNamespacedStatefulSetAsync("owned");
        using var cancelSecond = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var second = source.WatchAsync(Context with
        {
            TargetConnectionVersion = 2
        }, cancelSecond.Token)
            .GetAsyncEnumerator();
        await NextAsync(second, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.Equal("replacement", second.Current.BindingId);
        cancelSecond.Cancel();
        await second.DisposeAsync();
    }

    internal static async Task NextAsync(IAsyncEnumerator<ResourceDeploymentObservationChange> changes,
        ResourceDeploymentObservationChangeKind kind)
    {
        for (var count = 0; count < 100; count++)
        {
            Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            if (changes.Current.Kind == kind)
                return;
        }

        Assert.Fail($"No {kind} transition.");
    }

    private static DeploymentTargetLookupDto Target(long version) => new("target",
        "Target",
        "cluster",
        "kubeconfig",
        version,
        new DeploymentTargetWorkloadConfigurationLookupDto(1,
            null,
            new DeploymentTargetNetworkSourcesDto(ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)),
        1,
        true,
        true,
        false,
        "Healthy");

    private static RedisResourceDeploymentChangeSource Source(ClientProvider provider,
        Budget? budget = null,
        Func<DeploymentTargetLookupDto>? target = null) => new(
        (tenant, id, _) =>
        {
            Assert.Equal(Resource.TenantId, tenant);
            Assert.Equal(Resource.Id, id);
            return Task.FromResult<Redis?>(Resource);
        },
        provider,
        (_, _) => Task.FromResult<LookupDeploymentTargetOutcome>((target ?? (() => Target(1)))()),
        budget ?? new Budget(),
        TimeProvider.System);

    private sealed class ClientProvider(Func<IKubernetes> client) : IKubernetesClientProvider
    {
        public int Reads { get; private set; }

        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default)
        {
            Assert.Equal("target", id);
            Reads++;
            return Task.FromResult(client());
        }

        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException();

        public Task<IKubernetes> GetForCleanupAsync(string id, CancellationToken ct = default) =>
            throw new InvalidOperationException();
    }

    private sealed class Budget : IObservationReadBudget
    {
        public int Reads => Volatile.Read(ref _reads);
        public int Active => Volatile.Read(ref _active);

        public async Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _reads);
            try
            {
                return await read(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private int _active;
        private int _reads;
    }
}

internal sealed class RedisNativeTestApi : DelegatingHandler
{
    private readonly ConcurrentDictionary<string, NativeWatchTestStream> _streams = new(StringComparer.Ordinal);

    public ConcurrentQueue<Uri> Requests { get; } = new();

    public IEnumerable<NativeWatchTestStream> Streams => _streams.Values;

    public bool ControllerPresent { get; set; } = true;

    public string? ControllerMismatch { get; set; }

    public string ControllerUid { get; set; } = "controller";

    public string ServiceUid { get; set; } = "service";

    public int PodCount { get; set; } = 1;

    public int SliceCount { get; set; } = 1;

    public bool IncludeForeignPod { get; set; }

    public int? ReplaceAtRead { get; set; }

    public int ControllerReads { get; private set; }

    public NativeWatchTestStream Stream(string kind, string? uid = null) => _streams[kind + ":" + uid];

    private static string? WarningUid(Uri uri) => Uri.UnescapeDataString(uri.Query)
        .Split("involvedObject.uid=", StringSplitOptions.None).Last().Split(',', '&')[0];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Enqueue(uri);
        var kind = uri.AbsolutePath.Split('/').Last();
        if (KubernetesNativeWatchTests.IsWatch(uri))
        {
            Assert.Contains(kind, new[] { "statefulsets", "pods", "services", "events", "endpointslices" });
            var stream = new NativeWatchTestStream();
            _streams[kind + ":" + (kind == "events" ? WarningUid(uri) : null)] = stream;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(stream)
            });
        }

        var data = kind switch
        {
            "cache" => KubernetesJson.Serialize(ReadController()),
            "statefulsets" => KubernetesJson.Serialize(new V1StatefulSetList
            {
                Metadata = ListMeta("controller-list"),
                Items = ControllerPresent ? [Controller()] : []
            }),
            "pods" => KubernetesJson.Serialize(new V1PodList
            {
                Metadata = ListMeta("pods-list"),
                Items = Enumerable.Range(0, PodCount).Select(i => Pod($"pod-{i:00}"))
                    .Concat(IncludeForeignPod ? [Pod("foreign", "foreign")] : []).ToArray()
            }),
            "services" => KubernetesJson.Serialize(new V1ServiceList
            {
                Metadata = ListMeta("services-list"),
                Items = [Service()]
            }),
            "events" => KubernetesJson.Serialize(new Corev1EventList
            {
                Metadata = ListMeta("events-" + WarningUid(uri)),
                Items = []
            }),
            "endpointslices" => KubernetesJson.Serialize(new V1EndpointSliceList
            {
                Metadata = ListMeta("slices-" + ServiceUid),
                Items = Enumerable.Range(0, SliceCount).Select(i => Slice($"slice-{i}")).ToArray()
            }),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected native request {uri.AbsolutePath}")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(data, Encoding.UTF8, "application/json")
        });
    }

    private static V1ListMeta ListMeta(string version) => new() { ResourceVersion = version };

    private V1StatefulSet ReadController()
    {
        ControllerReads++;
        var item = Controller();
        if (ControllerReads >= ReplaceAtRead)
            item.Metadata.Uid = "replacement";
        return item;
    }

    internal V1ObjectMeta Metadata(string name, string uid, string? parent = null) => new()
    {
        Name = name,
        Uid = uid,
        NamespaceProperty = "owned",
        ResourceVersion = "opaque-" + uid,
        Generation = 2,
        Labels = RedisNativeObservationTests.Identity.ToDictionary(),
        Annotations = new Dictionary<string, string>
        {
            [KubernetesWorkloadObservation.TemplateGenerationAnnotation] = "2"
        },
        OwnerReferences = parent is null
            ? []
            :
            [
                new V1OwnerReference
                    { Uid = parent, Name = "parent", Kind = "StatefulSet", ApiVersion = "apps/v1", Controller = true }
            ]
    };

    internal V1StatefulSet Controller()
    {
        var meta = Metadata("cache", ControllerUid);
        if (ControllerMismatch == "namespace")
            meta.NamespaceProperty = "foreign";
        else if (ControllerMismatch == "name")
            meta.Name = "foreign";
        else if (ControllerMismatch == "uid")
            meta.Uid = "";
        else if (ControllerMismatch is { } key)
            meta.Labels[key] = "foreign";
        return new()
        {
            ApiVersion = "apps/v1",
            Kind = "StatefulSet",
            Metadata = meta,
            Spec = new V1StatefulSetSpec
            {
                Replicas = 1,
                Template = new V1PodTemplateSpec { Metadata = Metadata("template", "template") }
            },
            Status = new V1StatefulSetStatus
            {
                ObservedGeneration = 2,
                CurrentRevision = "current",
                UpdateRevision = "current",
                Replicas = 1,
                UpdatedReplicas = 1,
                ReadyReplicas = 1,
                AvailableReplicas = 1
            }
        };
    }

    internal V1Pod Pod(string uid, string? parent = null) => new()
    {
        ApiVersion = "v1",
        Kind = "Pod",
        Metadata = Metadata(uid, uid, parent ?? ControllerUid),
        Spec = new V1PodSpec { Containers = [new V1Container { Name = "redis", Image = "native-canary-image" }] },
        Status = new V1PodStatus
        {
            Phase = "Running",
            Conditions = [new V1PodCondition { Type = "Ready", Status = "True" }],
            ContainerStatuses =
            [
                new V1ContainerStatus
                    { Name = "redis", Ready = true, Image = "native-canary-image", ImageID = "native-canary-id" }
            ]
        }
    };

    internal V1Service Service() => new()
    {
        ApiVersion = "v1",
        Kind = "Service",
        Metadata = Metadata("cache-svc", ServiceUid)
    };

    internal V1EndpointSlice Slice(string uid) => new()
    {
        ApiVersion = "discovery.k8s.io/v1",
        Kind = "EndpointSlice",
        AddressType = "IPv4",
        Endpoints = [],
        Ports = [],
        Metadata = new V1ObjectMeta
        {
            Name = uid,
            Uid = uid,
            NamespaceProperty = "owned",
            ResourceVersion = "opaque-" + uid,
            Labels = new Dictionary<string, string> { ["kubernetes.io/service-name"] = "cache-svc" },
            OwnerReferences =
            [
                new V1OwnerReference
                    { Name = "cache-svc", Uid = ServiceUid, Kind = "Service", ApiVersion = "v1", Controller = true }
            ]
        }
    };

    internal static string Frame<T>(string kind, T value) =>
        $"{{\"type\":\"{kind}\",\"object\":{KubernetesJson.Serialize(value)}}}\n";

    protected override void Dispose(bool disposing)
    {
        foreach (var stream in _streams.Values)
            stream.Dispose();
        base.Dispose(disposing);
    }
}
