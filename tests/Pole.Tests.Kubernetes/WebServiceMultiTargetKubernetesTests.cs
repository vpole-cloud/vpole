using Pole.Features.Core.Kubernetes;
using System.Collections.Immutable;
using k8s;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Application.Kubernetes;
using Pole.Features.WebServices.Domain;
using Pole.Testing;
using Pole.Workloads;
using Xunit;
using KubernetesClient = k8s.Kubernetes;

namespace Pole.Tests.Kubernetes;

[Collection(KubernetesLabCollection.Name)]
[Trait(TestTraits.Layer, TestTraits.Layers.Kubernetes)]
[Trait(TestTraits.Concern, "MultiCluster")]
[Trait(TestTraits.Scope, TestTraits.Scopes.MultiCluster)]
[Trait(TestTraits.Feature, "WebServices")]
public sealed class WebServiceMultiTargetKubernetesTests(KubernetesClusterFixture fixture)
{
    [KubernetesFact("multi-cluster", "monitoring")]
    public async Task Deployment_succeeds_on_two_targets_and_failed_target_can_be_retried()
    {
        var targets = new[]
        {
            fixture.State.GetRequiredTarget("workload-a"),
            fixture.State.GetRequiredTarget("workload-b")
        };
        var clients = targets.Select(target => new KubernetesClient(target.CreateConfiguration())).ToArray();
        var identity = fixture.Identity;
        var namespaceName = identity.CreateNamespaceName("web-multi");
        var webService = BuildWebService();
        await using var resources = new TestResourceScope("multi-target WebService deployment");
        resources.Defer("dispose Kubernetes clients", _ =>
        {
            foreach (var client in clients)
                client.Dispose();
            return Task.CompletedTask;
        });
        var namespaces = resources.CreateConcurrentCleanupGroup("owned namespaces");

        foreach (var client in clients)
        {
            await OwnedNamespaceManager.CreateTrackedAsync(
                namespaces,
                client,
                namespaceName,
                identity);
        }

        var first = await ApplyAsync(clients[0], targets[0], webService, namespaceName);
        Assert.IsType<WebServiceKubernetesApplied>(first.Value);
        var healthyBefore = await clients[0].AppsV1.ReadNamespacedDeploymentAsync(
            webService.Name,
            namespaceName);
        var healthyUid = healthyBefore.Metadata.Uid;
        var healthyGeneration = healthyBefore.Metadata.Generation;

        var unavailableConfiguration = targets[1].CreateConfiguration();
        unavailableConfiguration.Host = "http://127.0.0.1:1";
        using var unavailable = new KubernetesClient(unavailableConfiguration);
        var failed = await ApplyAsync(unavailable, targets[1], webService, namespaceName);
        var failure = Assert.IsType<WebServiceKubernetesApplyFailed>(failed.Value);
        Assert.Contains("applying", failure.Message, StringComparison.Ordinal);

        var retried = await ApplyAsync(clients[1], targets[1], webService, namespaceName);
        Assert.IsType<WebServiceKubernetesApplied>(retried.Value);
        var targetBDeployment = await clients[1].AppsV1.ReadNamespacedDeploymentAsync(
            webService.Name,
            namespaceName);
        var healthyAfter = await clients[0].AppsV1.ReadNamespacedDeploymentAsync(
            webService.Name,
            namespaceName);

        Assert.Equal("release-1", targetBDeployment.Spec.Template.Metadata.Annotations["pole.server/releaseId"]);
        Assert.Equal(healthyUid, healthyAfter.Metadata.Uid);
        Assert.Equal(healthyGeneration, healthyAfter.Metadata.Generation);
        Assert.Equal("release-1", healthyAfter.Spec.Template.Metadata.Annotations["pole.server/releaseId"]);
    }

    private static Task<WebServiceKubernetesApplyOutcome> ApplyAsync(
        IKubernetes client,
        KubernetesLabTarget target,
        WebService webService,
        string namespaceName) =>
        WebServiceKubernetesResourceManager.ApplyAsync(
            client,
            webService.ToKubernetes(
                namespaceName,
                ResourceDeploymentIngressConfiguration.Create(
                    "traefik",
                    "pole-lab",
                    target.PublicBaseDomain,
                    [$"multi-{namespaceName}.{target.PublicBaseDomain}"]),
                BuildCompute(target.Key),
                fixedReplicas: 1,
                new WorkloadIdentityLabels(
                    webService.TenantId,
                    webService.ResourceGroupId,
                    "scope-44",
                    "cell-44",
                    target.Key,
                    "WebService",
                    webService.Id,
                    "deployment-44")),

            WebServiceResourceDeploymentMetadata.Create(
                "release-1",
                "deployment-44",
                "cell-44",
                target.Key,
                1,
                1,
                DateTime.UtcNow));

    private static WebService BuildWebService() =>
        WebServiceFactory.Create(
            name: "multi-target-web",
            tenantId: "507f1f77bcf86cd799439011",
            resourceGroupId: "507f1f77bcf86cd799439012",
            isolationScopeId: "507f1f77bcf86cd799439013",
            spec: new WebServiceSpec(
                new ImageInfo("docker.io", "library/nginx", "1.27.5-alpine"),
                new WebServiceConfig(
                    ImmutableDictionary<string, string>.Empty,
                    [new PortMapping(80, "80", "TCP", "http")]),
                Autoscaling: null,
                new ProbeSpec(
                    new ProbeConfig("/", "80"),
                    new ProbeConfig("/", "80"),
                    new ProbeConfig("/", "80"))),
            exposure: WebServiceExposure.Create(WebServiceExposureMode.Public),
            options: WebServiceFactory.DefaultOptions with
            {
                Id = "507f1f77bcf86cd799439014"
            });

    private static ResourceDeploymentComputeConfiguration BuildCompute(string targetKey) =>
        new(
            "507f1f77bcf86cd799439015",
            1,
            targetKey,
            "Lab",
            new ResourceDeploymentComputeResourceValues(50, 32),
            new ResourceDeploymentComputeResourceValues(250, 128),
            ComputeArchitecture.Any,
            [],
            [],
            [],
            [],
            [],
            RuntimeClassName: null);
}
