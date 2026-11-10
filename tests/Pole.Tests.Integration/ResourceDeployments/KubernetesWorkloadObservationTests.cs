using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class KubernetesWorkloadObservationTests
{
    private static readonly WorkloadIdentityLabels Identity = new(
        "tenant",
        "group",
        "scope",
        "cell",
        "target",
        "Test",
        "resource",
        "deployment");

    [Theory]
    [InlineData(2, false, true)]
    [InlineData(1, false, false)]
    [InlineData(2, true, false)]
    public async Task Readiness_requires_complete_current_rollout_and_owned_pods(
        int podGeneration,
        bool truncated,
        bool expectedReady)
    {
        var controller = new V1Deployment
        {
            Metadata = Metadata("application", "controller"),
            Spec = new V1DeploymentSpec
                { Replicas = 1, Template = new V1PodTemplateSpec { Metadata = Metadata("template", "template") } },
            Status = new V1DeploymentStatus
                { ObservedGeneration = 3, Replicas = 1, UpdatedReplicas = 1, ReadyReplicas = 1, AvailableReplicas = 1 },
        };
        var replica = new V1ReplicaSet { Metadata = Metadata("application-rs", "replica", "controller") };
        var pod = Pod("current", "pod", "replica", podGeneration);
        var foreign = Pod("foreign-secret-canary", "foreign", "replica", 2);
        foreign.Metadata.Labels["pole.io/tenant-id"] = "other-tenant";
        var unrelated = Pod("unrelated-secret-canary", "unrelated", "other-controller", 2);
        using var handler = new ReadHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/application", StringComparison.Ordinal)
                ?
                KubernetesJson.Serialize(controller)
                : request.RequestUri.AbsolutePath.EndsWith("/replicasets", StringComparison.Ordinal)
                    ? KubernetesJson.Serialize(new V1ReplicaSetList { Metadata = new V1ListMeta(), Items = [replica] })
                    : request.RequestUri.AbsolutePath.EndsWith("/pods", StringComparison.Ordinal)
                        ? KubernetesJson.Serialize(
                            new V1PodList
                            {
                                Metadata = new V1ListMeta
                                    { ContinueProperty = truncated ? "secret-canary-cursor" : null },
                                Items = [pod, foreign, unrelated],
                            })
                        : "{\"apiVersion\":\"v1\",\"kind\":\"EventList\",\"metadata\":{},\"items\":[]}");
        using var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://observation.test" },
            handler);
        var observed = Assert.IsType<ObservedWorkload>(
            await KubernetesWorkloadObservation.DeploymentAsync(
                client,
                "application",
                "owned",
                Identity,
                2,
                CancellationToken.None));
        Assert.Equal(expectedReady, observed.Ready);
        Assert.Equal(truncated, observed.PodsTruncated);
        Assert.Equal("current", Assert.Single(observed.Pods).Name);
        Assert.Equal(4, observed.Pods[0].RestartCount);
        Assert.Equal("Other", Assert.Single(observed.Pods[0].Reasons));
        Assert.DoesNotContain("secret-canary", JsonSerializer.Serialize(observed));
        var podQuery = Assert.Single(
            handler.Requests,
            uri => uri.AbsolutePath.EndsWith("/pods", StringComparison.Ordinal));
        foreach (var key in Identity.ToDictionary().Keys)
            Assert.Contains(key, Uri.UnescapeDataString(podQuery.Query));
        Assert.Contains("limit=100", podQuery.Query);
        foreach (var uri in handler.Requests.Where(uri => uri.AbsolutePath.EndsWith(
                     "/events",
                     StringComparison.Ordinal)))
        {
            Assert.Contains("involvedObject.uid=", Uri.UnescapeDataString(uri.Query));
            Assert.Contains("limit=4", uri.Query);
            Assert.DoesNotContain("foreign", uri.Query);
        }
    }

    [Fact]
    public async Task Ownership_mismatch_stops_before_pod_or_event_reads()
    {
        var controller = new V1StatefulSet { Metadata = Metadata("database", "controller") };
        controller.Metadata.Labels["pole.io/resource-deployment-id"] = "other";
        using var handler = new ReadHandler(_ => KubernetesJson.Serialize(controller));
        using var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://observation.test" },
            handler);
        var result = await KubernetesWorkloadObservation.StatefulSetAsync(
            client,
            "database",
            "owned",
            Identity,
            2,
            KubernetesWorkloadObservation.TemplateGenerationAnnotation,
            false,
            CancellationToken.None);
        Assert.Equal("OwnershipMismatch", Assert.IsType<UnavailableWorkloadObservation>(result).Reason);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Terminating_or_old_generation_pod_never_proves_current_readiness()
    {
        var pod = Pod("old", "pod", "controller", 1);
        pod.Metadata.DeletionTimestamp = DateTime.UtcNow;
        var result = KubernetesWorkloadObservation.SummarizePod(
            pod,
            2,
            KubernetesWorkloadObservation.TemplateGenerationAnnotation,
            false);
        Assert.False(result.Ready);
        Assert.False(result.CurrentGeneration);
        Assert.Equal("Terminating", result.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatefulSet_checks_both_provider_generation_locations_and_preserves_readiness_if_events_forbidden(
        bool generationLabel)
    {
        var key = generationLabel
            ? "pole.io/deployment-generation"
            : KubernetesWorkloadObservation.TemplateGenerationAnnotation;
        var controller = new V1StatefulSet
        {
            Metadata = Metadata("database", "controller"),
            Spec = new V1StatefulSetSpec
                { Replicas = 1, Template = new V1PodTemplateSpec { Metadata = Metadata("template", "template") } },
            Status = new V1StatefulSetStatus
            {
                ObservedGeneration = 3, Replicas = 1, UpdatedReplicas = 1, ReadyReplicas = 1, AvailableReplicas = 1,
                CurrentRevision = "current", UpdateRevision = "current",
            },
        };
        var pod = Pod("database-0", "pod", "controller", 2);
        if (generationLabel)
        {
            controller.Spec.Template.Metadata.Labels[key] = "2";
            pod.Metadata.Labels[key] = "2";
        }

        using var handler = new ReadHandler(
            request => request.RequestUri!.AbsolutePath.EndsWith("/database", StringComparison.Ordinal)
                ?
                KubernetesJson.Serialize(controller)
                : request.RequestUri.AbsolutePath.EndsWith("/pods", StringComparison.Ordinal)
                    ? KubernetesJson.Serialize(new V1PodList { Metadata = new V1ListMeta(), Items = [pod] })
                    : "{\"apiVersion\":\"v1\",\"kind\":\"Status\",\"message\":\"secret-canary\",\"code\":403}",
            HttpStatusCode.Forbidden);
        using var client = new Kubernetes(
            new KubernetesClientConfiguration { Host = "http://observation.test" },
            handler);
        var result = Assert.IsType<ObservedWorkload>(
            await KubernetesWorkloadObservation.StatefulSetAsync(
                client,
                "database",
                "owned",
                Identity,
                2,
                key,
                generationLabel,
                CancellationToken.None));
        Assert.True(result.Ready);
        Assert.Equal("Forbidden", result.EventsUnavailableReason);
        Assert.DoesNotContain("secret-canary", JsonSerializer.Serialize(result));
    }

    private static V1ObjectMeta Metadata(
        string name,
        string uid,
        string? parent = null,
        int generation = 2) => new()
    {
        Name = name,
        Uid = uid,
        NamespaceProperty = "owned",
        Generation = 3,
        Labels = Identity.ToDictionary(),
        Annotations = new Dictionary<string, string>
            { [KubernetesWorkloadObservation.TemplateGenerationAnnotation] = generation.ToString() },
        OwnerReferences = parent is null
            ? []
            :
            [
                new V1OwnerReference
                    { ApiVersion = "apps/v1", Kind = "Deployment", Name = "parent", Uid = parent, Controller = true },
            ],
    };

    private static V1Pod Pod(
        string name,
        string uid,
        string parent,
        int generation) => new()
    {
        Metadata = Metadata(name, uid, parent, generation),
        Status = new V1PodStatus
        {
            Phase = "Running",
            Conditions = [new V1PodCondition { Type = "Ready", Status = "True" }],
            ContainerStatuses =
            [
                new V1ContainerStatus
                {
                    Name = "app", Ready = true, RestartCount = 4, Image = "secret-canary-image",
                    ImageID = "secret-canary-image-id",
                    LastState = new V1ContainerState
                    {
                        Terminated = new V1ContainerStateTerminated
                            { ExitCode = 1, Reason = "secret-canary-reason", Message = "secret-canary-message" },
                    },
                },
            ],
        },
    };

    private sealed class ReadHandler(
        Func<HttpRequestMessage, string> response,
        HttpStatusCode eventsStatus = HttpStatusCode.OK
    ) : DelegatingHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Requests.Add(request.RequestUri!);
            return Task.FromResult(
                new HttpResponseMessage(
                    request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal)
                        ? eventsStatus
                        : HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(response(request), Encoding.UTF8, "application/json"),
                });
        }
    }
}
