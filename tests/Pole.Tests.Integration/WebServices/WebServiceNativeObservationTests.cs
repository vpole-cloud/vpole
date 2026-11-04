using Pole.Observation;
using Pole.Features.Secrets.Contracts;
using System.Collections.Immutable;
using System.Text.Json;
using k8s;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.ResourceDeployments;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceNativeObservationTests
{
    private const string TenantId = "507f1f77bcf86cd799439010";
    private const string ResourceId = "507f1f77bcf86cd799439011";
    private const string GroupId = "507f1f77bcf86cd799439012";
    private const string ScopeId = "507f1f77bcf86cd799439013";

    internal static readonly ResourceDeploymentObservationContext Context = new(
        new ResourceDeploymentReconciliationSnapshot(
            "deployment",
            TenantId,
            WebServiceResourceDeployment.ResourceType,
            ResourceId,
            GroupId,
            ScopeId,
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

    internal static readonly WorkloadIdentityLabels Identity = new(TenantId,
        GroupId,
        ScopeId,
        "cell",
        "target",
        WebServiceResourceDeployment.KubernetesResourceTypeLabel,
        ResourceId,
        "deployment");

    internal static readonly WebService Resource = WebServiceFactory.Create("application",
        TenantId,
        GroupId,
        ScopeId,
        new WebServiceSpec(new ImageInfo("registry", "application",
            Tag: "latest",
            SecretName: null),
            new WebServiceConfig()
            {
                EnvironmentVariables = ImmutableDictionary<string, string>.Empty,
                PortMappings = [],
                ConnectionEnvironmentReferences = ImmutableDictionary<string, ConnectionEnvironmentReference>.Empty,
                SecretEnvironmentReferences = ImmutableDictionary<string, SecretEnvironmentReference>.Empty
            },
            null,
            new ProbeSpec(new ProbeConfig("/", "8080"), new ProbeConfig("/", "8080"), new ProbeConfig("/", "8080"))),
        WebServiceExposure.Create(),
        WebServiceFactory.DefaultOptions with
        {
            Id = ResourceId,
            Revision = 2,
            ActiveRevision = 2
        });

    [Theory]
    [InlineData("ready", true)]
    [InlineData("waiting", false)]
    [InlineData("old-controller-generation", false)]
    [InlineData("old-terminating-pod", false)]
    [InlineData("old-orphaned-pod", false)]
    [InlineData("truncated", false)]
    [InlineData("retirement-scan-truncated", false)]
    [InlineData("native-unavailable", false)]
    public async Task Secret_key_retirement_requires_current_complete_native_rollout(string scenario, bool confirmed)
    {
        var podReads = 0;
        using var handler = new NativeWatchTestHandler(request =>
        {
            if (scenario == "native-unavailable") throw new HttpRequestException("Synthetic native outage");
            var json = NativeResponse(request, () =>
            {
                var controller = Controller();
                if (scenario == "waiting") controller.Status.ReadyReplicas = 0;
                if (scenario == "old-controller-generation") controller.Status.ObservedGeneration = 1;
                return controller;
            });
            if (request.RequestUri!.AbsolutePath.EndsWith("/pods", StringComparison.Ordinal))
            {
                var pods = KubernetesJson.Deserialize<V1PodList>(json);
                podReads++;
                if (scenario == "truncated" || scenario == "retirement-scan-truncated" && podReads > 1)
                    pods.Metadata.ContinueProperty = "more";
                if (scenario is "old-terminating-pod" or "old-orphaned-pod")
                {
                    var old = KubernetesJson.Deserialize<V1Pod>(KubernetesJson.Serialize(pods.Items[0]));
                    old.Metadata.Name = "old";
                    old.Metadata.Uid = "old";
                    old.Metadata.Annotations[KubernetesWorkloadObservation.TemplateGenerationAnnotation] = "1";
                    old.Metadata.DeletionTimestamp = DateTime.UtcNow;
                    if (scenario == "old-orphaned-pod") old.Metadata.OwnerReferences[0].Uid = "previous-controller-replica";
                    pods.Items.Add(old);
                }
                json = KubernetesJson.Serialize(pods);
            }
            return json;
        }, _ => throw new InvalidOperationException("Retirement must only sample."));
        using var client = KubernetesNativeWatchTests.Client(handler);
        var observer = new WebServiceSecretRolloutObserver((_, _, _) => Task.FromResult<WebService?>(Resource),
(request, _) => Task.FromResult<IReadOnlyList<PlacementSummaryDto>>([new("deployment", ScopeId, "cell", "target", 2, 2, ResourceDeploymentStatus.Applied, null, new(new("profile", 1), "test", new(100, 100), new(100, 100), []), [], ResourceRevision: 1, FixedReplicas: 1, Ingress: null, DeploymentRevision: 1, DesiredOperation: ResourceDeploymentDesiredOperation.Apply, Isolation: new ResourceDeploymentIsolationDto("Unknown", "EnforcementRequired", "EnforcementNotEstablished", null))]),
(request, _) => Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(new("deployment", ResourceId,
                "cell", "target", "owned", 3, 2, ResourceDeploymentTestData.ContractCompute("target"),
                ResourceDeploymentStatus.Applied, ResourceDeploymentDesiredOperation.Apply, ScopeId)), new ClientProvider(client));
        Assert.Equal(confirmed, (await observer.ObserveAsync(TenantId, ResourceId, default)).Value is SecretConsumerRolloutConfirmed);
    }

    [Fact]
    public async Task Owned_initial_Deployment_binds_before_quiet_watch_ready_and_selector_loss_withdraws_prior_UID()
    {
        using var handler = new NativeObservationTestApi();
        using var client = KubernetesNativeWatchTests.Client(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var budget = new ReadBudget();
        var source = Source(client, budget);
        var changes = source.WatchAsync(Context, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(ResourceDeploymentObservationChangeKind.BindingChanged, changes.Current.Kind);
        Assert.Equal("controller", changes.Current.BindingId);
        await WebServiceNativeChangeSourceTests.NextAsync(changes, ResourceDeploymentObservationChangeKind.SourceReady);
        Assert.Equal(0, budget.Active);
        Assert.True(budget.Reads >= 6); // No read-budget slot held by the long-lived socket.
        var removed = Controller();
        removed.Metadata.Labels["pole.io/tenant-id"] = "foreign-native-canary";
        removed.Metadata.ResourceVersion = "deleted-version";
        var stream = handler.Stream("deployments");
        stream.Write(Frame("DELETED", removed));
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(ResourceDeploymentObservationChangeKind.BindingChanged, changes.Current.Kind);
        Assert.Null(changes.Current.BindingId);
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(changes.Current));
        cancellation.Cancel();
        await changes.DisposeAsync();
        Assert.True(stream.Disposed);
        var query = Uri.UnescapeDataString(handler.Requests.First().Query);
        foreach (var key in Identity.ToDictionary().Keys)
            Assert.Contains(key, query);
        Assert.Contains("metadata.name=application", query);
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
    public async Task Foreign_or_incomplete_initial_binding_never_claims_coverage(string field)
    {
        var foreign = Controller();
        if (field == "namespace")
            foreign.Metadata.NamespaceProperty = "foreign";
        else if (field == "uid")
            foreign.Metadata.Uid = "";
        else
            foreign.Metadata.Labels[field] = "foreign";
        using var stream = new NativeWatchTestStream();
        using var handler =
            new NativeWatchTestHandler(_ => KubernetesNativeWatchTests.List("list", null, foreign), _ => stream);
        using var client = KubernetesNativeWatchTests.Client(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changes = Source(client, new ReadBudget()).WatchAsync(Context, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await changes.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(ResourceDeploymentObservationChangeKind.Unavailable, changes.Current.Kind);
        Assert.Null(changes.Current.BindingId);
        Assert.Equal(1, handler.Requests.Count(KubernetesNativeWatchTests.IsWatch));
        cancellation.Cancel();
        await changes.DisposeAsync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Bound_sample_rejects_replacement_before_health_during_reads_or_after_diagnostics(
        int replacementAtRead)
    {
        var controllerReads = 0;
        using var handler = new NativeWatchTestHandler(request => NativeResponse(request,
                () =>
                {
                    var controller = Controller();
                    if (++controllerReads >= replacementAtRead)
                        controller.Metadata.Uid = "replacement";
                    return controller;
                }),
            _ => throw new InvalidOperationException("Sampling cannot watch."));
        using var client = KubernetesNativeWatchTests.Client(handler);
        var observer = new WebServiceResourceDeploymentObserver((tenant, id, _) =>
                Task.FromResult<WebService?>(Resource),
            new ClientProvider(client));
        var result = await observer.ObserveBoundAsync(Context, "controller");
        Assert.Null(result.BindingId);
        Assert.Equal("BindingChanged", Assert.IsType<UnavailableWorkloadObservation>(result.Observation).Reason);
        Assert.Equal("BindingChanged", Assert.IsType<UnavailableWorkloadDiagnostics>(result.Diagnostics).Reason);
        if (replacementAtRead == 1)
            Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task
        Bound_sample_retains_safe_health_and_discovery_only_when_same_controller_is_verified_at_both_ends()
    {
        using var handler = new NativeWatchTestHandler(request => NativeResponse(request, Controller),
            _ => throw new InvalidOperationException("Sampling cannot watch."));
        using var client = KubernetesNativeWatchTests.Client(handler);
        var observer = new WebServiceResourceDeploymentObserver((tenant, id, _) =>
                Task.FromResult<WebService?>(Resource),
            new ClientProvider(client));
        var result = await observer.ObserveBoundAsync(Context, "controller");
        Assert.Equal("controller", result.BindingId);
        Assert.True(Assert.IsType<ObservedWorkload>(result.Observation).Ready);
        var diagnostics = Assert.IsType<AvailableWorkloadDiagnostics>(result.Diagnostics);
        Assert.Equal("pod", Assert.Single(diagnostics.Instances).Uid);
        Assert.Equal("app", Assert.Single(diagnostics.Instances[0].Containers).Name);
        Assert.DoesNotContain("native-canary", JsonSerializer.Serialize(result));
        Assert.Equal(4,
            handler.Requests.Count(uri => uri.AbsolutePath.EndsWith("/application", StringComparison.Ordinal)));
    }

    private static WebServiceResourceDeploymentChangeSource Source(IKubernetes client, ReadBudget budget) => new(
        (tenant, id, _) =>
        {
            Assert.Equal(TenantId, tenant);
            Assert.Equal(ResourceId, id);
            return Task.FromResult<WebService?>(Resource);
        },
        new ClientProvider(client),
        (id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(new DeploymentTargetLookupDto(id,
            "Target",
            "cluster",
            "kubeconfig",
            1,
            new DeploymentTargetWorkloadConfigurationLookupDto(1,
                null,
                new DeploymentTargetNetworkSourcesDto(ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>
                    .Empty)),
            1,
            true,
            true,
            false,
            "Healthy",
        TechnicalName: "test-target",
        NetworkPolicy: DeploymentTargetNetworkPolicyDto.Unknown)),
        budget,
        TimeProvider.System);

    private static string NativeResponse(HttpRequestMessage request, Func<V1Deployment> controller)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/application", StringComparison.Ordinal))
            return KubernetesJson.Serialize(controller());
        if (path.EndsWith("/replicasets", StringComparison.Ordinal))
            return KubernetesJson.Serialize(new V1ReplicaSetList
            {
                Metadata = new V1ListMeta(),
                Items = [new V1ReplicaSet { Metadata = Metadata("replica", "replica", "controller") }]
            });
        if (path.EndsWith("/pods", StringComparison.Ordinal))
            return KubernetesJson.Serialize(new V1PodList
            {
                Metadata = new V1ListMeta(),
                Items =
                [
                    new V1Pod
                    {
                        Metadata = Metadata("application-pod", "pod", "replica"),
                        Spec = new V1PodSpec
                            { Containers = [new V1Container { Name = "app", Image = "native-canary-image" }] },
                        Status = new V1PodStatus
                        {
                            Phase = "Running", Conditions = [new V1PodCondition { Type = "Ready", Status = "True" }],
                            ContainerStatuses =
                            [
                                new V1ContainerStatus
                                {
                                    Name = "app", Ready = true, Image = "native-canary-image",
                                    ImageID = "native-canary-id"
                                }
                            ]
                        }
                    }
                ]
            });
        return "{\"apiVersion\":\"v1\",\"kind\":\"EventList\",\"metadata\":{},\"items\":[]}";
    }

    private static V1Deployment Controller() => new()
    {
        ApiVersion = "apps/v1",
        Kind = "Deployment",
        Metadata = Metadata("application", "controller"),
        Spec = new V1DeploymentSpec
        {
            Replicas = 1,
            Template = new V1PodTemplateSpec { Metadata = Metadata("template", "template") }
        },
        Status = new V1DeploymentStatus
        {
            ObservedGeneration = 2,
            Replicas = 1,
            UpdatedReplicas = 1,
            ReadyReplicas = 1,
            AvailableReplicas = 1
        }
    };

    private static V1ObjectMeta Metadata(string name, string uid, string? parent = null) => new()
    {
        Name = name,
        Uid = uid,
        NamespaceProperty = "owned",
        ResourceVersion = "opaque/version",
        Generation = 2,
        Labels = Identity.ToDictionary(),
        Annotations = new Dictionary<string, string>
        {
            [KubernetesWorkloadObservation.TemplateGenerationAnnotation] = "2"
        },
        OwnerReferences = parent is null
            ? []
            :
            [
                new V1OwnerReference
                    { Name = "parent", Uid = parent, Kind = "ReplicaSet", ApiVersion = "apps/v1", Controller = true }
            ]
    };

    private static string Frame(string type, V1Deployment item) =>
        $"{{\"type\":\"{type}\",\"object\":{KubernetesJson.Serialize(item)}}}\n";

    private sealed class ClientProvider(IKubernetes client) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default)
        {
            Assert.Equal("target", id);
            return Task.FromResult(client);
        }

        public Task<IKubernetes> GetForCleanupAsync(string id, CancellationToken ct = default) =>
            throw new InvalidOperationException();

        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException();
    }

    private sealed class ReadBudget : IObservationReadBudget
    {
        public int Reads { get; private set; }

        public int Active { get; private set; }

        public async Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct = default)
        {
            Reads++;
            Active++;
            try
            {
                return await read(ct);
            }
            finally
            {
                Active--;
            }
        }
    }
}
