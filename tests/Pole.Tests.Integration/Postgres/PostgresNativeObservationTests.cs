using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Models;
using Microsoft.FSharp.Core;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Postgres.Application.Clusters;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Observation;
using Pole.Tests.Integration.ResourceDeployments;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Postgres;

public sealed class PostgresNativeObservationTests
{
    [Theory]
    [InlineData(PostgresDeploymentMode.StatefulSet)]
    [InlineData(PostgresDeploymentMode.Operator)]
    public async Task Quiet_owned_controller_source_covers_exact_services_slices_and_direct_instance_UIDs(
        PostgresDeploymentMode mode)
    {
        using var api = new PostgresNativeTestApi(mode) { IncludeBootstrapPod = true };
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(api, client).WatchAsync(api.Context, cancellation.Token).GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.Equal("controller", changes.Current.BindingId);
        Assert.Equal(mode == PostgresDeploymentMode.Operator ? 6 : 8,
            api.Requests.Count(KubernetesNativeWatchTests.IsWatch));
        Assert.All(api.Streams, stream => Assert.Equal(0, stream.FramesWritten));
        Assert.DoesNotContain(api.Requests.Where(uri => uri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal)),
            uri => uri.Query.Contains("bootstrap", StringComparison.Ordinal));
        var query = Uri.UnescapeDataString(api.Requests.First().Query);
        foreach (var key in api.Identity.ToDictionary().Keys)
            Assert.Contains(key, query);
        Assert.Contains("metadata.name=" + api.Name, query);
        cancellation.Cancel();
        await changes.DisposeAsync();
        Assert.All(api.Streams, stream => Assert.True(stream.Disposed));
    }

    [Theory]
    [InlineData(PostgresDeploymentMode.StatefulSet)]
    [InlineData(PostgresDeploymentMode.Operator)]
    public async Task Empty_owned_controller_LIST_watches_creation_without_repeated_LIST_polling(
        PostgresDeploymentMode mode)
    {
        using var api = new PostgresNativeTestApi(mode) { ControllerPresent = false };
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(api, client).WatchAsync(api.Context, cancellation.Token).GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Equal(2, api.Requests.Count);
        api.ControllerPresent = true;
        api.Stream(api.ControllerKind).Write(api.ControllerFrame("ADDED"));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
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
    [InlineData("uid")]
    public async Task CNPG_foreign_controller_cannot_be_adopted_from_name_or_inherited_child_labels(string mismatch)
    {
        using var api = new PostgresNativeTestApi(PostgresDeploymentMode.Operator) { ControllerMismatch = mismatch };
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(api, client).WatchAsync(api.Context, cancellation.Token).GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Null(changes.Current.BindingId);
        Assert.Equal(2, api.Requests.Count);
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Theory]
    [InlineData(PostgresDeploymentMode.StatefulSet)]
    [InlineData(PostgresDeploymentMode.Operator)]
    public async Task Selector_loss_delivers_negative_binding_fence_and_retires_dependents(PostgresDeploymentMode mode)
    {
        using var api = new PostgresNativeTestApi(mode);
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(api, client).WatchAsync(api.Context, cancellation.Token).GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        api.ControllerMismatch = "pole.io/tenant-id";
        api.Stream(api.ControllerKind).Write(api.ControllerFrame("DELETED"));
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(ResourceDeploymentObservationChangeKind.BindingChanged, changes.Current.Kind);
        Assert.Null(changes.Current.BindingId);
        cancellation.Cancel();
        await changes.DisposeAsync();
        Assert.All(api.Streams, stream => Assert.True(stream.Disposed));
    }

    [Theory]
    [InlineData(PostgresDeploymentMode.StatefulSet)]
    [InlineData(PostgresDeploymentMode.Operator)]
    public async Task Direct_Pod_owner_loss_withdraws_UID_warning_and_101st_incremental_Pod_fails_closed(
        PostgresDeploymentMode mode)
    {
        using var api = new PostgresNativeTestApi(mode) { PodCount = 100 };
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(api, client).WatchAsync(api.Context, cancellation.Token).GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.Equal(8,
            api.Requests.Count(uri =>
                uri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) &&
                KubernetesNativeWatchTests.IsWatch(uri)));
        var warning = api.Stream("events", "pod-00");
        var foreign = api.Pod("pod-00", "foreign-controller");
        api.Stream("pods").Write(PostgresNativeTestApi.Frame("MODIFIED", foreign));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.True(warning.Disposed);
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        api.Stream("pods").Write(PostgresNativeTestApi.Frame("ADDED", api.Pod("replacement")));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Invalidated);
        api.Stream("pods").Write(PostgresNativeTestApi.Frame("ADDED", api.Pod("overflow")));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("NativeObjectLimit", failure.Message);
        Assert.All(api.Streams, stream => Assert.True(stream.Disposed));
        await changes.DisposeAsync();
    }

    [Theory]
    [InlineData(PostgresDeploymentMode.StatefulSet, 1)]
    [InlineData(PostgresDeploymentMode.StatefulSet, 2)]
    [InlineData(PostgresDeploymentMode.StatefulSet, 3)]
    [InlineData(PostgresDeploymentMode.StatefulSet, 4)]
    [InlineData(PostgresDeploymentMode.Operator, 1)]
    [InlineData(PostgresDeploymentMode.Operator, 2)]
    [InlineData(PostgresDeploymentMode.Operator, 3)]
    [InlineData(PostgresDeploymentMode.Operator, 4)]
    public async Task Exact_bound_sample_rejects_UID_replacement_around_health_and_runtime_sampling(
        PostgresDeploymentMode mode,
        int replacement)
    {
        using var api = new PostgresNativeTestApi(mode) { ReplaceAtRead = replacement };
        using var client = KubernetesNativeWatchTests.Client(api);
        var result = await Bound(api, client).ObserveBoundAsync(api.Context, "controller");
        Assert.Null(result.BindingId);
        Assert.Equal("BindingChanged", Assert.IsType<UnavailableWorkloadObservation>(result.Observation).Reason);
        Assert.IsType<UnavailableWorkloadDiagnostics>(result.Diagnostics);
    }

    [Theory]
    [InlineData(PostgresDeploymentMode.StatefulSet)]
    [InlineData(PostgresDeploymentMode.Operator)]
    public async Task Bound_sample_requires_current_generation_and_owned_instance_readiness_and_exports_no_raw_payload(
        PostgresDeploymentMode mode)
    {
        using var api = new PostgresNativeTestApi(mode) { IncludeBootstrapPod = true };
        using var client = KubernetesNativeWatchTests.Client(api);
        var observer = Bound(api, client);
        var result = await observer.ObserveBoundAsync(api.Context, "controller");
        Assert.Equal("controller", result.BindingId);
        Assert.True(Assert.IsType<ObservedWorkload>(result.Observation).Ready);
        Assert.Equal("pod-00",
            Assert.Single(Assert.IsType<AvailableWorkloadDiagnostics>(result.Diagnostics).Instances).Uid);
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(result));
        Assert.Equal(4, api.ControllerReads);
        api.CurrentCondition = false;
        Assert.False(Assert
            .IsType<ObservedWorkload>((await observer.ObserveBoundAsync(api.Context, "controller")).Observation).Ready);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("zero")]
    public async Task CNPG_unversioned_condition_requires_current_native_and_actual_Pod_PVC_intent(string generation)
    {
        using var api = new PostgresNativeTestApi(PostgresDeploymentMode.Operator)
        {
            UnversionedCondition = generation,
            IncludeBootstrapPod = true
        };
        using var client = KubernetesNativeWatchTests.Client(api);
        var result = await Bound(api, client).ObserveBoundAsync(api.Context, "controller");
        Assert.True(Assert.IsType<ObservedWorkload>(result.Observation).Ready);
        Assert.Equal("pod-00",
            Assert.Single(Assert.IsType<AvailableWorkloadDiagnostics>(result.Diagnostics).Instances).Uid);
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("resources")]
    [InlineData("selector")]
    [InlineData("affinity")]
    [InlineData("tolerations")]
    [InlineData("storage")]
    [InlineData("pvc-owner")]
    [InlineData("pvc-capacity")]
    [InlineData("pvc-class")]
    [InlineData("generation")]
    [InlineData("not-ready")]
    [InlineData("native-image")]
    [InlineData("native-instances")]
    [InlineData("native-resources")]
    [InlineData("native-storage")]
    [InlineData("native-change")]
    public async Task CNPG_current_labels_and_unversioned_condition_cannot_hide_old_actual_intent(string mismatch)
    {
        using var api = new PostgresNativeTestApi(PostgresDeploymentMode.Operator)
        {
            UnversionedCondition = "omitted",
            IntentMismatch = mismatch
        };
        using var client = KubernetesNativeWatchTests.Client(api);
        var result = await Bound(api, client).ObserveBoundAsync(api.Context, "controller");
        Assert.False(result.Observation is ObservedWorkload { Ready: true });
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("count-string")]
    [InlineData("count-overflow")]
    [InlineData("negative-count")]
    [InlineData("missing-spec")]
    [InlineData("bad-metadata")]
    public async Task Malformed_operator_scalars_produce_safe_unavailable_without_unsafe_JSON_access(string shape)
    {
        using var api = new PostgresNativeTestApi(PostgresDeploymentMode.Operator) { Malformed = shape };
        using var client = KubernetesNativeWatchTests.Client(api);
        var result = await ((IResourceDeploymentObserver)Observer(api, client)).ObserveAsync(api.Context.Deployment);
        Assert.Equal("InvalidNativeObservation", Assert.IsType<UnavailableWorkloadObservation>(result).Reason);
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("conditions-string")]
    [InlineData("condition-status-number")]
    [InlineData("condition-generation-string")]
    public async Task Invalid_operator_condition_shapes_cannot_make_workload_ready(string shape)
    {
        using var api = new PostgresNativeTestApi(PostgresDeploymentMode.Operator) { Malformed = shape };
        using var client = KubernetesNativeWatchTests.Client(api);
        var result = await ((IResourceDeploymentObserver)Observer(api, client)).ObserveAsync(api.Context.Deployment);
        Assert.False(Assert.IsType<ObservedWorkload>(result).Ready);
    }

    [Theory]
    [InlineData(PostgresDeploymentMode.StatefulSet)]
    [InlineData(PostgresDeploymentMode.Operator)]
    public async Task Service_UID_replacement_retires_slice_interest_and_foreign_slice_never_invalidates(
        PostgresDeploymentMode mode)
    {
        using var api = new PostgresNativeTestApi(mode);
        using var client = KubernetesNativeWatchTests.Client(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(api, client).WatchAsync(api.Context, cancellation.Token).GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        var serviceName = api.Name + "-rw";
        var old = api.Stream("endpointslices", serviceName);
        api.ServiceVersion = "replacement";
        api.Stream("services", serviceName).Write(PostgresNativeTestApi.Frame("MODIFIED", api.Service(serviceName)));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.True(old.Disposed);
        var slice = api.Slice("foreign-slice", serviceName);
        slice.Metadata.OwnerReferences[0].Uid = "foreign-service";
        var stream = api.Stream("endpointslices", serviceName);
        stream.Write(PostgresNativeTestApi.Frame("ADDED", slice));
        var next = changes.MoveNextAsync().AsTask();
        await stream.NextReadAfterFrame.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await next.WaitAsync(TimeSpan.FromSeconds(2)));
        await changes.DisposeAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Disabled_or_deleting_target_cannot_open_native_Cluster_requests(bool enabled, bool deleting)
    {
        using var api = new PostgresNativeTestApi(PostgresDeploymentMode.Operator);
        using var client = KubernetesNativeWatchTests.Client(api);
        var reads = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(api,
                () =>
                {
                    reads++;
                    return client;
                },
                () => Target(1) with { Enabled = enabled, DeletionRequested = deleting })
            .WatchAsync(api.Context, cancellation.Token).GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Equal(0, reads);
        Assert.Empty(api.Requests);
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Credential_rotation_during_client_acquisition_fails_post_lookup_before_any_native_request()
    {
        using var api = new PostgresNativeTestApi(PostgresDeploymentMode.Operator);
        using var client = KubernetesNativeWatchTests.Client(api);
        var version = 1L;
        var reads = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(api,
                () =>
                {
                    reads++;
                    version = 2;
                    return client;
                },
                () => Target(version))
            .WatchAsync(api.Context, cancellation.Token).GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Equal(1, reads);
        Assert.Empty(api.Requests);
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Rotation_fences_old_context_and_new_context_uses_fresh_client_without_disposing_cached_client()
    {
        using var firstApi = new PostgresNativeTestApi(PostgresDeploymentMode.Operator);
        using var secondApi = new PostgresNativeTestApi(PostgresDeploymentMode.Operator);
        using var firstClient = KubernetesNativeWatchTests.Client(firstApi);
        using var secondClient = KubernetesNativeWatchTests.Client(secondApi);
        var version = 1L;
        var reads = 0;
        var source = Source(firstApi,
            () =>
            {
                reads++;
                return version == 1 ? firstClient : secondClient;
            },
            () => Target(version));
        using var firstCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = source.WatchAsync(firstApi.Context, firstCancellation.Token).GetAsyncEnumerator();
        await NextAsync(first, ResourceDeploymentObservationChangeKind.SourceReady);
        var previousReads = reads;
        version = 2;
        firstApi.Stream("clusters").Complete();
        await NextAsync(first, ResourceDeploymentObservationChangeKind.Unavailable);
        await NextAsync(first, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Equal(previousReads, reads);
        firstCancellation.Cancel();
        await first.DisposeAsync();
        await firstClient.CustomObjects.ListNamespacedCustomObjectAsync<JsonElement>("postgresql.cnpg.io",
            "v1",
            "owned",
            "clusters");
        using var secondCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Retain the logical placement identity while switching the cached transport.
        secondApi.ResourceIdentity = firstApi;
        var second = source.WatchAsync(firstApi.Context with
        {
            TargetConnectionVersion = 2
        }, secondCancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(second, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.True(reads > previousReads);
        secondCancellation.Cancel();
        await second.DisposeAsync();
    }

    private static PostgresResourceDeploymentObserver Observer(PostgresNativeTestApi api, IKubernetes client) =>
        new(Load(api), new ClientProvider(() => client));

    private static IResourceDeploymentBoundObserver Bound(PostgresNativeTestApi api, IKubernetes client) =>
        Observer(api, client);

    private static LoadPostgresClusterById Load(PostgresNativeTestApi api) => new((tenant, id, _) =>
    {
        Assert.Equal(api.Resource.TenantId, tenant);
        Assert.Equal(api.Resource.Id, id);
        return Task.FromResult(FSharpOption<PostgresCluster>.Some(api.Resource));
    });

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
        "Healthy",
        TechnicalName: "test-target",
        NetworkPolicy: DeploymentTargetNetworkPolicyDto.Unknown);

    private static IResourceDeploymentObservationChangeSource Source(PostgresNativeTestApi api, IKubernetes client) =>
        Source(api, () => client);

    private static IResourceDeploymentObservationChangeSource Source(PostgresNativeTestApi api,
        Func<IKubernetes> client,
        Func<LookupDeploymentTargetOutcome>? target = null) => new PostgresResourceDeploymentChangeSource(
        Load(api),
        new ClientProvider(client),
        new LookupDeploymentTarget((id, _) =>
        {
            Assert.Equal("target", id);
            return Task.FromResult((target ?? (() => Target(1)))());
        }),
        new Budget(),
        TimeProvider.System);

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

    private sealed class ClientProvider(Func<IKubernetes> client) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default)
        {
            Assert.Equal("target", id);
            return Task.FromResult(client());
        }

        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException();

        public Task<IKubernetes> GetForCleanupAsync(string id, CancellationToken ct = default) =>
            throw new InvalidOperationException();
    }

    private sealed class Budget : IObservationReadBudget
    {
        public Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct = default) => read(ct);
    }
}

internal sealed class PostgresNativeTestApi : DelegatingHandler
{
    private readonly ConcurrentDictionary<string, NativeWatchTestStream> _streams = new(StringComparer.Ordinal);

    public ConcurrentQueue<Uri> Requests { get; } = new();

    public IEnumerable<NativeWatchTestStream> Streams => _streams.Values;
    private readonly PostgresCluster _resource;

    public PostgresNativeTestApi? ResourceIdentity { get; set; }

    public PostgresCluster Resource => ResourceIdentity?.Resource ?? _resource;

    public ResourceDeploymentObservationContext Context { get; }

    private readonly WorkloadIdentityLabels _identity;
    public WorkloadIdentityLabels Identity => ResourceIdentity?.Identity ?? _identity;
    public string Name => PostgresClusterKubernetesBuilder.buildResourceName(Resource.Id);
    public bool Operator => Resource.Spec.DeploymentMode == PostgresDeploymentMode.Operator;
    public string ControllerKind => Operator ? "clusters" : "statefulsets";

    public bool ControllerPresent { get; set; } = true;

    public string? ControllerMismatch { get; set; }

    public string ServiceVersion { get; set; } = "service";

    public int PodCount { get; set; } = 1;

    public bool IncludeBootstrapPod { get; set; }

    public int? ReplaceAtRead { get; set; }

    public int ControllerReads { get; private set; }

    public bool CurrentCondition { get; set; } = true;

    public string? Malformed { get; set; }

    public string? UnversionedCondition { get; set; }

    public string? IntentMismatch { get; set; }

    public PostgresNativeTestApi(PostgresDeploymentMode mode)
    {
        _resource = PostgresCluster.Create("507f1f77bcf86cd799439010",
            "database",
            "507f1f77bcf86cd799439012",
            17,
            mode,
            "profile",
            new CreatePostgresClusterOptions(FSharpOption<string>.Some("scope"), 1, 1));
        _identity = new(Resource.TenantId,
            Resource.GroupId,
            "scope",
            "cell",
            "target",
            "Postgres",
            Resource.Id,
            "deployment");
        Context = new(new ResourceDeploymentReconciliationSnapshot("deployment",
                Resource.TenantId,
                PostgresResourceDeployment.ResourceType,
                Resource.Id,
                Resource.GroupId,
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
                    new ResourceDeploymentStorageConfiguration("profile", 1, "owned-storage"),
                    ResourceDeploymentTestData.ContractCompute("target"))),
            3,
            1);
    }

    public NativeWatchTestStream Stream(string kind, string? key = null) => _streams[kind + ":" + key];

    private static string QueryValue(Uri uri, string marker)
    {
        var query = Uri.UnescapeDataString(uri.Query);
        var start = query.IndexOf(marker, StringComparison.Ordinal);
        return start < 0 ? "" : query[(start + marker.Length)..].Split(',', '&')[0];
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Enqueue(uri);
        var kind = uri.AbsolutePath.Split('/').Last();
        var key = kind switch
        {
            "events" => QueryValue(uri, "involvedObject.uid="),
            "services" => QueryValue(uri, "metadata.name="),
            "endpointslices" => QueryValue(uri, "kubernetes.io/service-name="),
            _ => null
        };
        if (KubernetesNativeWatchTests.IsWatch(uri))
        {
            Assert.Contains(kind, new[] { ControllerKind, "pods", "services", "events", "endpointslices" });
            var stream = new NativeWatchTestStream();
            _streams[kind + ":" + key] = stream;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(stream)
            });
        }

        string json;
        if (uri.AbsolutePath.Contains("/persistentvolumeclaims/", StringComparison.Ordinal))
            json = KubernetesJson.Serialize(Pvc());
        else if (kind == Name)
        {
            ControllerReads++;
            json = Operator
                ? ClusterJson(ControllerReads >= ReplaceAtRead ? "replacement" : "controller")
                : KubernetesJson.Serialize(StatefulSet(ControllerReads >= ReplaceAtRead
                    ? "replacement"
                    : "controller"));
        }
        else
            json = kind switch
            {
                "clusters" when Operator => JsonSerializer.Serialize(new
                {
                    apiVersion = "postgresql.cnpg.io/v1",
                    kind = "ClusterList",
                    metadata = new
                    {
                        resourceVersion = "cluster-list"
                    },
                    items = ControllerPresent
                        ? new[] { JsonSerializer.Deserialize<JsonElement>(ClusterJson("controller")) }
                        : []
                }),
                "statefulsets" when !Operator => KubernetesJson.Serialize(new V1StatefulSetList
                {
                    Metadata = ListMeta("stateful-list"),
                    Items = ControllerPresent ? [StatefulSet("controller")] : []
                }),
                "pods" => KubernetesJson.Serialize(new V1PodList
                {
                    Metadata = ListMeta("pods-list"),
                    Items = Enumerable.Range(0, PodCount).Select(i => Pod($"pod-{i:00}"))
                        .Concat(IncludeBootstrapPod ? [Pod("bootstrap", "bootstrap-job")] : []).ToArray()
                }),
                "services" => KubernetesJson.Serialize(new V1ServiceList
                {
                    Metadata = ListMeta("service-list-" + key),
                    Items = [Service(key!)]
                }),
                "events" => KubernetesJson.Serialize(new Corev1EventList
                {
                    Metadata = ListMeta("event-list-" + key),
                    Items = []
                }),
                "endpointslices" => KubernetesJson.Serialize(new V1EndpointSliceList
                {
                    Metadata = ListMeta("slice-list-" + key),
                    Items = [Slice("slice", key!)]
                }),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected native request {uri.AbsolutePath}")
            };

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }

    private static V1ListMeta ListMeta(string version) => new() { ResourceVersion = version };

    private V1ObjectMeta Metadata(string name, string uid, string? parent = null) => new()
    {
        Name = name,
        Uid = uid,
        NamespaceProperty = "owned",
        Generation = 2,
        ResourceVersion = "opaque-" + uid,
        Labels = new Dictionary<string, string>(Identity.ToDictionary()) { ["pole.io/deployment-generation"] = "2" },
        Annotations = new Dictionary<string, string> { ["raw-native-canary"] = "native-canary-secret" },
        OwnerReferences = parent is null
            ? []
            :
            [
                new V1OwnerReference
                {
                    Uid = parent, Name = "parent",
                    Kind = parent == "bootstrap-job" ? "Job" : Operator ? "Cluster" : "StatefulSet",
                    ApiVersion =
                        parent == "bootstrap-job" ? "batch/v1" : Operator ? "postgresql.cnpg.io/v1" : "apps/v1",
                    Controller = true
                }
            ]
    };

    private V1ObjectMeta ControllerMetadata(string uid)
    {
        var meta = Metadata(Name, uid);
        if (ControllerMismatch == "namespace")
            meta.NamespaceProperty = "foreign";
        else if (ControllerMismatch == "uid")
            meta.Uid = "";
        else if (ControllerMismatch is { } key)
            meta.Labels[key] = "foreign";
        return meta;
    }

    private V1StatefulSet StatefulSet(string uid) => new()
    {
        ApiVersion = "apps/v1",
        Kind = "StatefulSet",
        Metadata = ControllerMetadata(uid),
        Spec = new V1StatefulSetSpec
        {
            Replicas = 1,
            Template = new V1PodTemplateSpec { Metadata = Metadata("template", "template") }
        },
        Status = new V1StatefulSetStatus
        {
            ObservedGeneration = CurrentCondition ? 2 : 1,
            CurrentRevision = "current",
            UpdateRevision = "current",
            Replicas = 1,
            UpdatedReplicas = 1,
            ReadyReplicas = 1,
            AvailableReplicas = 1
        }
    };

    private string ClusterJson(string uid)
    {
        var node = JsonNode.Parse(KubernetesJson.Serialize(new
        {
            apiVersion = "postgresql.cnpg.io/v1",
            kind = "Cluster",
            metadata = ControllerMetadata(uid),
            spec = new
            {
                instances = IntentMismatch == "native-instances" ? 2 : 1,
                secret = "native-canary-spec",
                imageName = IntentMismatch == "native-image"
                    ? "native-canary-old"
                    : "ghcr.io/cloudnative-pg/postgresql:17",
                resources = Requirements(IntentMismatch == "native-resources"),
                storage = new
                {
                    size = IntentMismatch == "native-storage" ? "2Gi" : "1Gi",
                    storageClass = "owned-storage"
                }
            },
            status = new
            {
                instances = 1,
                readyInstances = 1,
                conditions = new[]
                {
                    new
                    {
                        type = "Ready", status = "True", observedGeneration = CurrentCondition ? 2 : 1,
                        message = "native-canary-condition"
                    }
                }
            }
        }))!;
        if (UnversionedCondition == "omitted")
            node["status"]!["conditions"]![0]!.AsObject().Remove("observedGeneration");
        else if (UnversionedCondition == "zero")
            node["status"]!["conditions"]![0]!["observedGeneration"] = 0;
        if (IntentMismatch == "native-change" && ControllerReads >= 3)
            node["metadata"]!["generation"] = 3;
        switch (Malformed)
        {
            case "count-string":
                node["spec"]!["instances"] = "native-canary";
                break;
            case "count-overflow":
                node["spec"]!["instances"] = long.MaxValue;
                break;
            case "negative-count":
                node["status"]!["readyInstances"] = -1;
                break;
            case "missing-spec":
                node.AsObject().Remove("spec");
                break;
            case "bad-metadata":
                node["metadata"] = "native-canary";
                break;
            case "conditions-string":
                node["status"]!["conditions"] = "native-canary";
                break;
            case "condition-status-number":
                node["status"]!["conditions"]![0]!["status"] = 42;
                break;
            case "condition-generation-string":
                node["status"]!["conditions"]![0]!["observedGeneration"] = "native-canary";
                break;
        }

        return node.ToJsonString();
    }

    public string ControllerFrame(string kind) => Operator
        ? $"{{\"type\":\"{kind}\",\"object\":{ClusterJson("controller")}}}\n"
        : Frame(kind, StatefulSet("controller"));

    private static V1ResourceRequirements Requirements(bool stale = false) => new()
    {
        Requests = new Dictionary<string, ResourceQuantity>
        {
            ["cpu"] = new(stale ? "100m" : "250m"),
            ["memory"] = new("256Mi")
        },
        Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("1"), ["memory"] = new("1Gi") }
    };

    private V1PersistentVolumeClaim Pvc()
    {
        var meta = Metadata("data", "pvc", IntentMismatch == "pvc-owner" ? "foreign" : "controller");
        return new()
        {
            Metadata = meta,
            Spec = new()
            {
                StorageClassName = IntentMismatch == "pvc-class" ? "foreign" : "owned-storage",
                Resources = new V1VolumeResourceRequirements
                {
                    Requests = new Dictionary<string, ResourceQuantity>
                    {
                        ["storage"] = new(IntentMismatch == "storage" ? "2Gi" : "1Gi")
                    }
                }
            },
            Status = new()
            {
                Phase = "Bound",
                Capacity = new Dictionary<string, ResourceQuantity>
                {
                    ["storage"] = new(IntentMismatch == "pvc-capacity" ? "512Mi" : "1Gi")
                }
            }
        };
    }

    public V1Pod Pod(string uid, string parent = "controller")
    {
        var meta = Metadata(uid, uid, parent);
        if (IntentMismatch == "generation")
            meta.Labels["pole.io/deployment-generation"] = "1";
        return new()
        {
            ApiVersion = "v1",
            Kind = "Pod",
            Metadata = meta,
            Spec = new()
            {
                Containers =
                [
                    new V1Container
                    {
                        Name = "postgres",
                        Image =
                            IntentMismatch == "image" ? "native-canary-old" : "ghcr.io/cloudnative-pg/postgresql:17",
                        Resources = Requirements(IntentMismatch == "resources"),
                        VolumeMounts = [new() { Name = "pgdata", MountPath = "/var/lib/postgresql/data" }]
                    }
                ],
                Volumes = [new() { Name = "pgdata", PersistentVolumeClaim = new() { ClaimName = "data" } }],
                NodeSelector = IntentMismatch == "selector"
                    ? new Dictionary<string, string> { ["old"] = "selector" }
                    : null,
                Affinity = IntentMismatch == "affinity"
                    ? new V1Affinity
                    {
                        NodeAffinity = new V1NodeAffinity
                        {
                            PreferredDuringSchedulingIgnoredDuringExecution =
                            [
                                new()
                                {
                                    Weight = 1,
                                    Preference = new V1NodeSelectorTerm
                                        { MatchExpressions = [new() { Key = "old", OperatorProperty = "Exists" }] }
                                }
                            ]
                        }
                    }
                    : null,
                Tolerations = IntentMismatch == "tolerations"
                    ? [new() { Key = "old", OperatorProperty = "Exists" }]
                    : null
            },
            Status = new()
            {
                Phase = "Running",
                Conditions = [new() { Type = "Ready", Status = IntentMismatch == "not-ready" ? "False" : "True" }],
                ContainerStatuses =
                [
                    new()
                    {
                        Name = "postgres", Ready = true, Image = "native-canary-image",
                        ImageID = "native-canary-image-id"
                    }
                ]
            }
        };
    }

    public V1Service Service(string name) => new()
    {
        ApiVersion = "v1",
        Kind = "Service",
        Metadata = Metadata(name, ServiceVersion + name, Operator ? "controller" : null)
    };

    public V1EndpointSlice Slice(string uid, string serviceName) => new()
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
            Labels = new Dictionary<string, string> { ["kubernetes.io/service-name"] = serviceName },
            OwnerReferences =
            [
                new V1OwnerReference
                {
                    Name = serviceName, Uid = ServiceVersion + serviceName, Kind = "Service", ApiVersion = "v1",
                    Controller = true
                }
            ]
        }
    };

    public static string Frame<T>(string kind, T value) =>
        $"{{\"type\":\"{kind}\",\"object\":{KubernetesJson.Serialize(value)}}}\n";

    protected override void Dispose(bool disposing)
    {
        foreach (var stream in _streams.Values)
            stream.Dispose();
        base.Dispose(disposing);
    }
}
