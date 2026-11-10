using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Domain;
using Pole.Observation;
using Pole.Testing.Kubernetes;
using Xunit;

namespace Pole.Features.WebServices.Tests;

public sealed class WebServiceNativeChangeSourceTests
{
    private static ResourceDeploymentObservationContext Context => WebServiceNativeObservationTests.Context;

    [Fact]
    public async Task Empty_controller_watch_stays_open_and_owned_added_recovers_all_source_coverage()
    {
        using var api = new NativeObservationTestApi { ControllerPresent = false };
        using var client = NativeWatchTestClient.Create(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client), _ => Target(1))
            .WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Single(api.Requests, NativeWatchTestClient.IsWatch);
        Assert.Equal(1, api.Requests.Count(uri => !NativeWatchTestClient.IsWatch(uri)));
        api.ControllerPresent = true;
        api.Stream("deployments").Write(NativeObservationTestApi.Frame("ADDED", api.Controller()));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.BindingChanged);
        Assert.Equal("controller", changes.Current.BindingId);
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.Equal(
            8,
            api.Requests.Count(NativeWatchTestClient.IsWatch)); // Five native kinds + three UID Warning interests.
        Assert.Equal(1, api.Streams.Sum(stream => stream.FramesWritten));
        cancellation.Cancel();
        await changes.DisposeAsync();
        Assert.All(api.Streams, stream => Assert.True(stream.Disposed));
    }

    [Fact]
    public async Task Coverage_waits_for_every_required_HTTP200_even_when_other_streams_are_healthy_and_idle()
    {
        using var api = new NativeObservationTestApi { DelayServiceHeaders = true };
        using var client = NativeWatchTestClient.Create(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client), _ => Target(1))
            .WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        var ready = NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        await api.ServiceHeadersEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(ready.IsCompleted);
        api.ReleaseServiceHeaders.TrySetResult();
        await ready.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("controller", changes.Current.BindingId);
        Assert.Contains(
            api.Requests,
            uri => Uri.UnescapeDataString(uri.Query)
                .Contains("metadata.name=application-svc", StringComparison.Ordinal));
        Assert.Contains(
            api.Requests,
            uri => Uri.UnescapeDataString(uri.Query)
                .Contains("metadata.name=application-ing", StringComparison.Ordinal));
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Only_verified_controller_replica_pod_UIDs_get_at_most_eight_warning_interests()
    {
        using var api = new NativeObservationTestApi { PodCount = 12, IncludeForeignOwner = true };
        using var client = NativeWatchTestClient.Create(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client), _ => Target(1))
            .WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        var eventRequests = api.Requests
            .Where(uri => uri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(8, eventRequests.Count(NativeWatchTestClient.IsWatch));
        foreach (var uri in eventRequests)
        {
            var query = Uri.UnescapeDataString(uri.Query);
            Assert.Contains("involvedObject.uid=", query);
            Assert.Contains("type=Warning", query);
            Assert.DoesNotContain("foreign", query);
        }

        foreach (var uri in api.Requests.Where(NativeWatchTestClient.IsWatch))
        {
            var kind = uri.AbsolutePath.Split('/').Last();
            var version = kind switch
            {
                "deployments" => "deployment-list",
                "replicasets" => "replica-list",
                "pods" => "pod-list",
                "services" => "service-list",
                "ingresses" => "ingress-list",
                "events" => "event-list-" +
                    Uri.UnescapeDataString(uri.Query).Split("involvedObject.uid=")[1].Split(',', '&')[0],
                _ => throw new InvalidOperationException(),
            };
            Assert.Contains("resourceVersion=" + version, Uri.UnescapeDataString(uri.Query));
        }

        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Pod_selector_loss_retires_its_warning_stream_and_replacement_UID_starts_a_fresh_interest()
    {
        using var api = new NativeObservationTestApi();
        using var client = NativeWatchTestClient.Create(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client), _ => Target(1))
            .WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        var oldWarning = api.Stream("events", "pod-00");
        var removed = api.Pod("pod-00");
        removed.Metadata.Labels["pole.io/resource-id"] = "foreign-native-canary";
        removed.Metadata.ResourceVersion = "pod-delete";
        api.Stream("pods").Write(NativeObservationTestApi.Frame("DELETED", removed));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Invalidated);
        Assert.True(oldWarning.Disposed);
        var replacement = api.Pod("replacement-pod");
        api.Stream("pods").Write(NativeObservationTestApi.Frame("ADDED", replacement));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.False(api.Stream("events", "replacement-pod").Disposed);
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(changes.Current));
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Replica_selector_loss_withdraws_dependent_Pod_warnings_without_adopting_foreign_owner_chains()
    {
        using var api = new NativeObservationTestApi { IncludeForeignOwner = true };
        using var client = NativeWatchTestClient.Create(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client), _ => Target(1))
            .WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        var podWarning = api.Stream("events", "pod-00");
        var replicaWarning = api.Stream("events", "replica");
        var removed = api.Replica("replica", "controller");
        removed.Metadata.Labels["pole.io/tenant-id"] = "foreign";
        removed.Metadata.ResourceVersion = "replica-deleted";
        api.Stream("replicasets").Write(NativeObservationTestApi.Frame("DELETED", removed));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Invalidated);
        Assert.True(podWarning.Disposed);
        Assert.True(replicaWarning.Disposed);
        Assert.DoesNotContain(
            api.Requests,
            uri => uri.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) &&
                uri.Query.Contains("foreign", StringComparison.Ordinal));
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Theory]
    [InlineData("pods")]
    [InlineData("replicasets")]
    [InlineData("events")]
    public async Task Incremental_101st_owned_UID_withdraws_coverage_drains_streams_and_requires_fresh_LIST(string kind)
    {
        using var api = new NativeObservationTestApi
        {
            PodCount = kind == "pods" ? 100 : 1,
            ReplicaCount = kind == "replicasets" ? 100 : 1,
            EventCount = kind == "events" ? 100 : 0,
        };
        using var client = NativeWatchTestClient.Create(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var source = Source(new ClientProvider(() => client), _ => Target(1));
        var changes = source.WatchAsync(Context, cancellation.Token).GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        var stream = api.Stream(kind, kind == "events" ? "controller" : null);
        var frame = kind switch
        {
            "pods" => NativeObservationTestApi.Frame("ADDED", api.Pod("overflow-pod")),
            "replicasets" => NativeObservationTestApi.Frame("ADDED", api.Replica("overflow-replica", "controller")),
            "events" => NativeObservationTestApi.Frame("ADDED", api.Warning("overflow-event", "controller")),
            _ => throw new InvalidOperationException(),
        };
        stream.Write(frame);
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Equal("controller", changes.Current.BindingId);
        var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
            await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("NativeObjectLimit", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.All(api.Streams, item => Assert.True(item.Disposed));
        await changes.DisposeAsync();

        // A coordinator restart cannot accept an incomplete101-object list as healthy coverage.
        if (kind == "pods")
            api.PodCount = 101;
        else if (kind == "replicasets")
            api.ReplicaCount = 101;
        else
            api.EventCount = 101;
        using var overLimitCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var overLimit = source.WatchAsync(Context, overLimitCancellation.Token).GetAsyncEnumerator();
        await NextAsync(overLimit, ResourceDeploymentObservationChangeKind.Unavailable);
        overLimitCancellation.Cancel();
        await overLimit.DisposeAsync();
        if (kind == "pods")
            api.PodCount = 100;
        else if (kind == "replicasets")
            api.ReplicaCount = 100;
        else
            api.EventCount = 100;
        var recovered = source.WatchAsync(Context, cancellation.Token).GetAsyncEnumerator();
        await NextAsync(recovered, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.Equal("controller", recovered.Current.BindingId);
        cancellation.Cancel();
        await recovered.DisposeAsync();
    }

    [Theory]
    [InlineData("uid")]
    [InlineData("name")]
    [InlineData("parent")]
    public async Task Oversized_native_metadata_disables_coverage_without_exporting_or_retaining_the_value(string field)
    {
        using var api = new NativeObservationTestApi();
        using var client = NativeWatchTestClient.Create(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client), _ => Target(1))
            .WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        var item = api.Pod("large-pod");
        var value = "native-canary-" + new string('x', 300);
        if (field == "uid")
            item.Metadata.Uid = value;
        else if (field == "name")
            item.Metadata.Name = value;
        else
            item.Metadata.OwnerReferences[0].Uid = value;
        api.Stream("pods").Write(NativeObservationTestApi.Frame("ADDED", item));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(changes.Current));
        cancellation.Cancel();
        await changes.DisposeAsync();
        Assert.All(api.Streams, stream => Assert.True(stream.Disposed));
    }

    [Theory]
    [InlineData("wrong-object")]
    [InlineData("Normal")]
    public async Task Unverified_warning_frame_cannot_invalidate_the_workload(string mismatch)
    {
        using var api = new NativeObservationTestApi();
        using var client = NativeWatchTestClient.Create(api);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(new ClientProvider(() => client), _ => Target(1))
            .WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        var stream = api.Stream("events", "pod-00");
        var item = new Corev1Event
        {
            ApiVersion = "v1",
            Kind = "Event",
            Metadata = api.Metadata("warning", "warning-event"),
            InvolvedObject = new V1ObjectReference { Uid = mismatch == "wrong-object" ? "foreign" : "pod-00" },
            Type = mismatch == "Normal" ? "Normal" : "Warning",
            Reason = "native-canary-reason",
            Message = "native-canary-message",
        };
        stream.Write(NativeObservationTestApi.Frame("ADDED", item));
        var next = changes.MoveNextAsync().AsTask();
        // SDK parsed/projected the frame and requested its next line before the negative assertion.
        await stream.NextReadAfterFrame.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await next.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, stream.FramesWritten);
        await changes.DisposeAsync();
    }

    [Fact]
    public async Task Rotation_fences_old_context_and_new_interest_uses_fresh_client_without_disposing_cached_client()
    {
        using var firstApi = new NativeObservationTestApi();
        using var secondApi = new NativeObservationTestApi { ControllerUid = "new-controller" };
        using var firstClient = NativeWatchTestClient.Create(firstApi);
        using var secondClient = NativeWatchTestClient.Create(secondApi);
        var version = 1L;
        var provider = new ClientProvider(() => version == 1 ? firstClient : secondClient);
        var source = Source(provider, _ => Target(version));
        using var firstCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = source.WatchAsync(Context, firstCancellation.Token).GetAsyncEnumerator();
        await NextAsync(first, ResourceDeploymentObservationChangeKind.SourceReady);
        var beforeRotation = provider.Reads;
        version = 2;
        firstApi.Stream("deployments").Complete();
        await NextAsync(first, ResourceDeploymentObservationChangeKind.Unavailable);
        await NextAsync(
            first,
            ResourceDeploymentObservationChangeKind
                .Unavailable); // Reopen rechecks durable target before asking for a client.
        Assert.Equal(beforeRotation, provider.Reads);
        firstCancellation.Cancel();
        await first.DisposeAsync();
        await firstClient.AppsV1.ListNamespacedDeploymentAsync("owned");
        using var secondCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var second = source.WatchAsync(
                Context with
                {
                    TargetConnectionVersion = 2,
                },
                secondCancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(second, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.Equal("new-controller", second.Current.BindingId);
        Assert.True(provider.Reads > beforeRotation);
        secondCancellation.Cancel();
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Transient_lookup_failure_disables_coverage_then_restarts_a_fresh_LIST_without_reconnect()
    {
        using var api = new NativeObservationTestApi();
        using var client = NativeWatchTestClient.Create(api);
        var calls = 0;
        var source = Source(
            new ClientProvider(() => client),
            _ => ++calls == 1 ? throw new InvalidOperationException("lookup-native-canary") : Target(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = source.WatchAsync(Context, cancellation.Token).GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Empty(api.Requests);
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(changes.Current));
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.Equal("controller", changes.Current.BindingId);
        Assert.Equal(8, api.Requests.Count(NativeWatchTestClient.IsWatch));
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Disabled_or_deleting_target_never_opens_native_reads(bool enabled, bool deleting)
    {
        using var api = new NativeObservationTestApi();
        using var client = NativeWatchTestClient.Create(api);
        var provider = new ClientProvider(() => client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(provider, _ => Target(1) with { Enabled = enabled, DeletionRequested = deleting })
            .WatchAsync(Context, cancellation.Token)
            .GetAsyncEnumerator();
        await NextAsync(changes, ResourceDeploymentObservationChangeKind.Unavailable);
        Assert.Equal(0, provider.Reads);
        Assert.Empty(api.Requests);
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    internal static async Task NextAsync(
        IAsyncEnumerator<ResourceDeploymentObservationChange> changes,
        ResourceDeploymentObservationChangeKind kind)
    {
        for (var count = 0; count < 100; count++)
        {
            Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
            if (changes.Current.Kind == kind)
                return;
        }

        Assert.Fail($"No {kind} transition in bounded source output.");
    }

    private static DeploymentTargetLookupDto Target(long version) => new(
        "target",
        "Target",
        "cluster",
        "kubeconfig",
        version,
        new DeploymentTargetWorkloadConfigurationLookupDto(
            1,
            null,
            new DeploymentTargetNetworkSourcesDto(ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>.Empty)),
        1,
        true,
        true,
        false,
        "Healthy",
        TechnicalName: "test-target",
        NetworkPolicy: DeploymentTargetNetworkPolicyDto.Unknown);

    private static WebServiceResourceDeploymentChangeSource Source(
        ClientProvider provider,
        Func<string, DeploymentTargetLookupDto> target) => new(
        (
            _, _,
            _) => Task.FromResult<WebService?>(WebServiceNativeObservationTests.Resource),
        provider,
        (id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(target(id)),
        new ReadBudget(),
        TimeProvider.System);

    private sealed class ClientProvider(
        Func<IKubernetes> client
    ) : IKubernetesClientProvider
    {
        public int Reads { get; private set; }

        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default)
        {
            Assert.Equal("target", id);
            Reads++;
            return Task.FromResult(client());
        }

        public Task<IKubernetes> GetForCleanupAsync(string id, CancellationToken ct = default) =>
            throw new InvalidOperationException();

        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException();
    }

    private sealed class ReadBudget : IObservationReadBudget
    {
        public Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct = default) => read(ct);
    }
}

internal sealed class NativeObservationTestApi : DelegatingHandler
{
    private readonly ConcurrentDictionary<string, NativeWatchTestStream> _streams = new(StringComparer.Ordinal);

    public ConcurrentQueue<Uri> Requests { get; } = new();

    public IEnumerable<NativeWatchTestStream> Streams => _streams.Values;

    public bool ControllerPresent { get; set; } = true;

    public string ControllerUid { get; set; } = "controller";

    public int PodCount { get; set; } = 1;

    public int ReplicaCount { get; set; } = 1;

    public int EventCount { get; set; }

    public bool IncludeForeignOwner { get; set; }

    public bool DelayServiceHeaders { get; set; }

    public TaskCompletionSource ServiceHeadersEntered { get; }
        = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseServiceHeaders { get; }
        = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NativeWatchTestStream Stream(string kind, string? uid = null) => _streams[Key(kind, uid)];
    private static string Key(string kind, string? uid) => kind + ":" + uid;

    private static string? EventUid(Uri uri)
    {
        var query = Uri.UnescapeDataString(uri.Query);
        const string marker = "involvedObject.uid=";
        var offset = query.IndexOf(marker, StringComparison.Ordinal);
        return offset < 0 ? null : query[(offset + marker.Length)..].Split(',', '&')[0];
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!);
        var kind = request.RequestUri!.AbsolutePath.Split('/').Last();
        if (NativeWatchTestClient.IsWatch(request.RequestUri))
        {
            if (kind == "services" && DelayServiceHeaders)
            {
                ServiceHeadersEntered.TrySetResult();
                await ReleaseServiceHeaders.Task.WaitAsync(cancellationToken);
            }

            var stream = new NativeWatchTestStream();
            _streams[Key(kind, kind == "events" ? EventUid(request.RequestUri) : null)] = stream;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(stream),
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(List(kind, request.RequestUri), Encoding.UTF8, "application/json"),
        };
    }

    private string List(string kind, Uri uri) => kind switch
    {
        "deployments" => KubernetesJson.Serialize(
            new V1DeploymentList
            {
                Metadata = ListMetadata("deployment-list"),
                Items = ControllerPresent ? [Controller()] : [],
            }),
        "replicasets" => KubernetesJson.Serialize(
            new V1ReplicaSetList
            {
                Metadata = ListMetadata("replica-list"),
                Items = Enumerable.Range(0, ReplicaCount)
                    .Select(index => Replica(ReplicaCount == 1 ? "replica" : $"replica-{index:00}", ControllerUid))
                    .Concat(IncludeForeignOwner ? [Replica("foreign-replica", "foreign-controller")] : [])
                    .ToArray(),
            }),
        "pods" => KubernetesJson.Serialize(
            new V1PodList
            {
                Metadata = ListMetadata("pod-list"),
                Items = Enumerable.Range(0, PodCount)
                    .Select(index => Pod($"pod-{index:00}"))
                    .Concat(IncludeForeignOwner ? [Pod("foreign-pod", "foreign-replica")] : [])
                    .ToArray(),
            }),
        "services" => KubernetesJson.Serialize(
            new V1ServiceList
            {
                Metadata = ListMetadata("service-list"),
                Items = [new V1Service { Metadata = Metadata("application-svc", "service") }],
            }),
        "ingresses" => KubernetesJson.Serialize(
            new V1IngressList
            {
                Metadata = ListMetadata("ingress-list"),
                Items = [],
            }),
        "events" => KubernetesJson.Serialize(
            new Corev1EventList
            {
                Metadata = ListMetadata("event-list-" + EventUid(uri)),
                Items = Enumerable.Range(0, EventCount)
                    .Select(index => Warning($"warning-{index:00}", EventUid(uri)!))
                    .ToArray(),
            }),
        _ => throw new InvalidOperationException($"Unexpected native request {kind}."),
    };

    private static V1ListMeta ListMetadata(string version) => new() { ResourceVersion = version };

    public V1ObjectMeta Metadata(
        string name,
        string uid,
        string? parent = null) => new()
    {
        Name = name,
        Uid = uid,
        NamespaceProperty = "owned",
        ResourceVersion = "opaque-" + uid,
        Labels = WebServiceNativeObservationTests.Identity.ToDictionary(),
        OwnerReferences = parent is null
            ? []
            :
            [
                new V1OwnerReference
                    { Uid = parent, Name = "parent", ApiVersion = "apps/v1", Kind = "ReplicaSet", Controller = true },
            ],
    };

    public V1Deployment Controller() => new()
    {
        ApiVersion = "apps/v1",
        Kind = "Deployment",
        Metadata = Metadata("application", ControllerUid),
    };

    public V1ReplicaSet Replica(string uid, string parent) => new()
    {
        ApiVersion = "apps/v1",
        Kind = "ReplicaSet",
        Metadata = Metadata(uid, uid, parent),
    };

    public V1Pod Pod(string uid, string parent = "replica") => new()
    {
        ApiVersion = "v1",
        Kind = "Pod",
        Metadata = Metadata(uid, uid, parent),
    };

    public Corev1Event Warning(string uid, string involvedUid) => new()
    {
        ApiVersion = "v1",
        Kind = "Event",
        Metadata = Metadata(uid, uid),
        Type = "Warning",
        InvolvedObject = new V1ObjectReference { Uid = involvedUid },
        Reason = "Other",
    };

    public static string Frame<T>(string type, T item) =>
        $"{{\"type\":\"{type}\",\"object\":{KubernetesJson.Serialize(item)}}}\n";

    protected override void Dispose(bool disposing)
    {
        foreach (var stream in _streams.Values)
            stream.Dispose();
        base.Dispose(disposing);
    }
}
