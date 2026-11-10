using k8s;
using Pole.Testing;
using Xunit;

namespace Pole.Tests.Kubernetes;

[Collection(KubernetesLabCollection.Name)]
[Trait(TestTraits.Layer, TestTraits.Layers.Kubernetes)]
[Trait(TestTraits.Concern, "Smoke")]
[Trait(TestTraits.Scope, TestTraits.Scopes.SingleCluster)]
[Trait(TestTraits.Feature, "KubernetesLab")]
public sealed class KubernetesLabSmokeTests(
    KubernetesClusterFixture fixture
)
{
    [KubernetesFact("api", "storage", "ingress", "registry", "dns")]
    public async Task Selected_target_exposes_the_baseline_lab_contract()
    {
        var namespaces = await fixture.Client.CoreV1.ListNamespaceAsync();
        var storageClass = await fixture.Client.StorageV1.ReadStorageClassAsync(fixture.Target.StorageClassName);
        var ingressClass = await fixture.Client.NetworkingV1.ReadIngressClassAsync("traefik");

        Assert.NotEmpty(namespaces.Items);
        Assert.Equal("local-path", storageClass.Metadata.Name);
        Assert.Equal("traefik", ingressClass.Metadata.Name);
        Assert.Equal("127.0.0.1:5051", fixture.State.Registry.Host);
        Assert.StartsWith("k3d-pole-lab-registry:", fixture.State.Registry.ClusterHost, StringComparison.Ordinal);
    }

    [KubernetesFact("monitoring")]
    public async Task Monitoring_overlay_installs_the_service_monitor_contract()
    {
        var definition = await fixture.Client.ApiextensionsV1.ReadCustomResourceDefinitionAsync(
            "servicemonitors.monitoring.coreos.com");

        Assert.Equal("ServiceMonitor", definition.Spec.Names.Kind);
    }

    [KubernetesFact("cnpg")]
    public async Task Cnpg_overlay_installs_the_cluster_contract()
    {
        var definition = await fixture.Client.ApiextensionsV1.ReadCustomResourceDefinitionAsync(
            "clusters.postgresql.cnpg.io");

        Assert.Equal("Cluster", definition.Spec.Names.Kind);
    }

    [KubernetesFact("cert-manager")]
    public async Task Cert_manager_overlay_installs_the_certificate_contract()
    {
        var definition = await fixture.Client.ApiextensionsV1.ReadCustomResourceDefinitionAsync(
            "certificates.cert-manager.io");

        Assert.Equal("Certificate", definition.Spec.Names.Kind);
    }

    [KubernetesFact("harbor")]
    public async Task Harbor_overlay_is_available_in_its_owned_namespace()
    {
        var deployments = await fixture.Client.AppsV1.ListNamespacedDeploymentAsync("pole-lab-harbor");
        var services = await fixture.Client.CoreV1.ListNamespacedServiceAsync("pole-lab-harbor");

        Assert.NotEmpty(deployments.Items);
        Assert.Contains(services.Items, service => service.Metadata.Name.Contains("core", StringComparison.Ordinal));
    }

    [KubernetesFact("buildkit")]
    public async Task Buildkit_overlay_uses_the_pinned_privileged_image()
    {
        var deployment = await fixture.Client.AppsV1.ReadNamespacedDeploymentAsync("buildkit", "pole-lab-build");

        Assert.Equal("docker.io/moby/buildkit:v0.31.2", deployment.Spec.Template.Spec.Containers.Single().Image);
        Assert.Equal(1, deployment.Status.AvailableReplicas);
    }
}
