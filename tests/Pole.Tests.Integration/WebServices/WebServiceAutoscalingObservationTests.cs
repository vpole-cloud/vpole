using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using k8s;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Domain;
using Pole.Observation;
using Pole.Tests.Integration.ResourceDeployments;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceAutoscalingObservationTests
{
    private static readonly ResourceDeploymentObservationContext Context = WebServiceNativeObservationTests.Context;
    private static readonly WebService Resource = WebServiceNativeObservationTests.Resource with
    {
        Spec = WebServiceNativeObservationTests.Resource.Spec with { Autoscaling = new(1, 5, new("cpu", 80)) }
    };

    [Theory]
    [InlineData("missing", "AutoscalerMissing")]
    [InlineData("policy", "AutoscalerConfigurationDrift")]
    [InlineData("owner", "AutoscalerOwnershipConflict")]
    public async Task Healthy_deployment_does_not_hide_autoscaler_failure(string change, string reason)
    {
        using var handler = new ObservationHandler();
        using var client = KubernetesNativeWatchTests.Client(handler);
        var observer = new WebServiceResourceDeploymentObserver((_, _, _) => Task.FromResult<WebService?>(Resource),
            new ClientProvider(client));
        Assert.True(Assert.IsType<ObservedWorkload>(await observer.ObserveAsync(Context.Deployment)).Ready);
        if (change == "missing")
            handler.Autoscaler = null;
        else if (change == "policy")
            handler.Autoscaler!.Spec.MaxReplicas++;
        else
            handler.Autoscaler!.Metadata.Labels["pole.io/resource-id"] = "foreign";

        var result = await observer.ObserveAsync(Context.Deployment);
        Assert.Equal(reason, Assert.IsType<UnavailableWorkloadObservation>(result).Reason);
    }

    [Fact]
    public async Task Native_autoscaler_changes_invalidate_bound_observation()
    {
        var streams = new ConcurrentDictionary<string, NativeWatchTestStream>();
        using var handler = new NativeWatchTestHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/deployments", StringComparison.Ordinal))
                return KubernetesJson.Serialize(new V1DeploymentList
                {
                    Metadata = new V1ListMeta { ResourceVersion = "1" },
                    Items = [Controller()]
                });
            if (path.EndsWith("/horizontalpodautoscalers", StringComparison.Ordinal))
                return KubernetesJson.Serialize(new V2HorizontalPodAutoscalerList
                {
                    Metadata = new V1ListMeta { ResourceVersion = "1" },
                    Items = [Autoscaler()]
                });
            return "{\"metadata\":{\"resourceVersion\":\"1\"},\"items\":[]}";
        }, request => streams.GetOrAdd(request.RequestUri!.AbsolutePath.Split('/')[^1], _ => new()));
        using var client = KubernetesNativeWatchTests.Client(handler);
        var source = new WebServiceResourceDeploymentChangeSource(
            (_, _, _) => Task.FromResult<WebService?>(Resource), new ClientProvider(client),
            (id, _) => Task.FromResult<LookupDeploymentTargetOutcome>(new DeploymentTargetLookupDto(id,
                "Target", "cluster", "kubeconfig", 1,
                new DeploymentTargetWorkloadConfigurationLookupDto(1, null,
                    new DeploymentTargetNetworkSourcesDto(ImmutableDictionary<string, DeploymentTargetNetworkSourceDto>
                        .Empty)), 1, true, true, false, "Healthy", "test-target",
                DeploymentTargetNetworkPolicyDto.Unknown)), new ReadBudget(), TimeProvider.System);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var changes = source.WatchAsync(Context, cancellation.Token).GetAsyncEnumerator();
        while (await changes.MoveNextAsync() &&
               changes.Current.Kind != ResourceDeploymentObservationChangeKind.SourceReady)
        {
        }

        Assert.Equal(ResourceDeploymentObservationChangeKind.SourceReady, changes.Current.Kind);
        var autoscaler = Autoscaler();
        autoscaler.Metadata.ResourceVersion = "2";
        streams["horizontalpodautoscalers"].Write(
            $"{{\"type\":\"DELETED\",\"object\":{KubernetesJson.Serialize(autoscaler)}}}\n");
        Assert.True(await changes.MoveNextAsync());
        Assert.Equal(ResourceDeploymentObservationChangeKind.Invalidated, changes.Current.Kind);
        Assert.Equal("controller", changes.Current.BindingId);
        cancellation.Cancel();
    }

    private static V1ObjectMeta Metadata(string name, string uid, string? parent = null) => new()
    {
        Name = name,
        Uid = uid,
        NamespaceProperty = "owned",
        ResourceVersion = "1",
        Generation = 2,
        Labels = WebServiceNativeObservationTests.Identity.ToDictionary(),
        Annotations = new Dictionary<string, string>
        {
            [KubernetesWorkloadObservation.TemplateGenerationAnnotation] = "2"
        },
        OwnerReferences = parent is null ? [] : [new V1OwnerReference
        {
            Name = "parent", Uid = parent, Kind = "ReplicaSet", ApiVersion = "apps/v1", Controller = true
        }]
    };

    private static V2HorizontalPodAutoscaler Autoscaler() => new()
    {
        ApiVersion = "autoscaling/v2",
        Kind = "HorizontalPodAutoscaler",
        Metadata = Metadata("application-hpa", "autoscaler"),
        Spec = new V2HorizontalPodAutoscalerSpec
        {
            MinReplicas = 1,
            MaxReplicas = 5,
            ScaleTargetRef = new V2CrossVersionObjectReference
            {
                Name = "application",
                Kind = "Deployment",
                ApiVersion = "apps/v1"
            },
            Metrics = [new V2MetricSpec
            {
                Type = "Resource", Resource = new V2ResourceMetricSource
                {
                    Name = "cpu", Target = new V2MetricTarget { Type = "Utilization", AverageUtilization = 80 }
                }
            }]
        }
    };

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

    private sealed class ObservationHandler : DelegatingHandler
    {
        public V2HorizontalPodAutoscaler? Autoscaler { get; set; } = WebServiceAutoscalingObservationTests.Autoscaler();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object? body = path.Split('/')[^1] switch
            {
                "application-hpa" => Autoscaler,
                "application" => Controller(),
                "events" => new Corev1EventList { Metadata = new V1ListMeta(), Items = [] },
                "replicasets" => new V1ReplicaSetList
                {
                    Metadata = new V1ListMeta(),
                    Items = [new V1ReplicaSet { Metadata = Metadata("replica", "replica", "controller") }]
                },
                "pods" => new V1PodList
                {
                    Metadata = new V1ListMeta(),
                    Items = [new V1Pod
                    {
                        Metadata = Metadata("pod", "pod", "replica"),
                        Spec = new V1PodSpec { Containers = [new V1Container { Name = "app", Image = "test" }] },
                        Status = new V1PodStatus
                        {
                            Phase = "Running", Conditions = [new V1PodCondition { Type = "Ready", Status = "True" }],
                            ContainerStatuses = [new V1ContainerStatus
                            {
                                Name = "app", Ready = true, Image = "test", ImageID = "test"
                            }]
                        }
                    }]
                },
                _ => throw new InvalidOperationException(path)
            };
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body is null ? "{}" : KubernetesJson.Serialize(body),
                    Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ClientProvider(IKubernetes client) : IKubernetesClientProvider
    {
        public Task<IKubernetes> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(client);
        public Task<IKubernetes> GetForCleanupAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(client);
        public Task<IKubernetes> GetDefaultAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ReadBudget : IObservationReadBudget
    {
        public Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct = default) => read(ct);
    }
}
