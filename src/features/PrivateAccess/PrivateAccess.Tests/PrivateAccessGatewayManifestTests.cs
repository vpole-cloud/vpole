using System.Net;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Runtime;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.PrivateAccess.Tests;

public sealed class PrivateAccessGatewayManifestTests
{
    private static readonly PrivateAccessGatewayIdentity Identity = new(
        "0123456789abcdef01234567",
        "89abcdef0123456701234567",
        "fedcba9876543210fedcba98",
        1,
        1);

    private static readonly PrivateAccessGatewaySettings Settings = new(
        "pole/gateway:test",
        new Uri("https://broker.example:9444"),
        [IPAddress.Parse("192.0.2.8")]);

    private static readonly PrivateAccessGatewayEndpoint Endpoint = new(
        "postgres",
        "cell-a",
        "deployment-a",
        "runtime-a",
        "database",
        NetworkPort.Create(5432),
        NetworkPort.Create(5432));

    [Fact]
    public void GatewayNamespaceAndBaseline_AreTenantTargetOwnedAndDenyAllTraffic()
    {
        var gatewayNamespace = PrivateAccessGatewayManifest.BuildNamespace(Identity);
        Assert.Equal("pole-access-0123456789abcdef01234567-89abcdef0123456701234567", gatewayNamespace.Metadata.Name);
        Assert.Equal(Identity.TenantId, gatewayNamespace.Metadata.Labels[PrivateAccessGatewayManifest.TenantLabel]);
        Assert.Equal(
            Identity.DeploymentTargetId,
            gatewayNamespace.Metadata.Labels[PrivateAccessGatewayManifest.TargetLabel]);
        Assert.Equal("restricted", gatewayNamespace.Metadata.Labels["pod-security.kubernetes.io/enforce"]);

        var baseline = PrivateAccessGatewayManifest.BuildBaseline(Identity);
        Assert.Equal(gatewayNamespace.Metadata.Name, baseline.Metadata.NamespaceProperty);
        Assert.Equal(["Ingress", "Egress"], baseline.Spec.PolicyTypes);
        Assert.Empty(baseline.Spec.Ingress);
        Assert.Empty(baseline.Spec.Egress);
        Assert.Null(baseline.Spec.PodSelector.MatchLabels);
        Assert.Null(baseline.Spec.PodSelector.MatchExpressions);
    }

    [Fact]
    public void GatewayDeployment_IsOneRestrictedPodWithSecretMountedIdentity()
    {
        var deployment = PrivateAccessGatewayManifest.BuildDeployment(Identity, Settings);
        Assert.Equal(1, deployment.Spec.Replicas);
        Assert.Equal("Recreate", deployment.Spec.Strategy.Type);
        Assert.Equal(deployment.Spec.Selector.MatchLabels, deployment.Spec.Template.Metadata.Labels);
        var pod = deployment.Spec.Template.Spec;
        Assert.False(pod.AutomountServiceAccountToken);
        Assert.False(pod.HostNetwork);
        Assert.True(pod.SecurityContext.RunAsNonRoot);
        Assert.Equal("RuntimeDefault", pod.SecurityContext.SeccompProfile.Type);
        var container = Assert.Single(pod.Containers);
        Assert.Equal(["gateway"], container.Args);
        Assert.False(container.SecurityContext.AllowPrivilegeEscalation);
        Assert.True(container.SecurityContext.ReadOnlyRootFilesystem);
        Assert.Equal(["ALL"], container.SecurityContext.Capabilities.Drop);
        Assert.Equal(
            PrivateAccessGatewayManifest.IdentityFile,
            Assert.Single(container.Env, env => env.Name == "POLE_ACCESS_IDENTITY_FILE").Value);
        Assert.DoesNotContain(container.Env, env => env.Name.Contains("TOKEN", StringComparison.Ordinal));
        Assert.Equal(PrivateAccessGatewayManifest.GatewayName, Assert.Single(pod.Volumes).Secret.SecretName);
        Assert.True(Assert.Single(container.VolumeMounts).ReadOnlyProperty);
    }

    [Fact]
    public void GenerationChange_RollsPodWithoutChangingDeploymentSelector()
    {
        var current = PrivateAccessGatewayManifest.BuildDeployment(Identity, Settings);
        var rotated = PrivateAccessGatewayManifest.BuildDeployment(
            Identity with { Generation = 2, Revision = 2 },
            Settings);
        Assert.Equal(current.Spec.Selector.MatchLabels, rotated.Spec.Selector.MatchLabels);
        Assert.NotEqual(
            current.Spec.Template.Metadata.Annotations[PrivateAccessGatewayManifest.GenerationAnnotation],
            rotated.Spec.Template.Metadata.Annotations[PrivateAccessGatewayManifest.GenerationAnnotation]);
    }

    [Fact]
    public void NamespaceRejectsNonCanonicalTenantId()
    {
        Assert.Throws<ArgumentException>(() =>
            PrivateAccessGatewayManifest.Namespace(Identity with { TenantId = "TENANT A" }));
        Assert.Throws<ArgumentException>(() =>
            PrivateAccessGatewayManifest.Namespace(Identity with { DeploymentTargetId = "target-a" }));
        Assert.Throws<ArgumentException>(() =>
            PrivateAccessGatewayManifest.Namespace(Identity with { GatewayId = "gateway-a" }));
    }

    [Fact]
    public void Egress_AllowsOnlyExactEndpointPortBrokerAddressesAndDns()
    {
        var policy = PrivateAccessGatewayManifest.BuildGatewayEgress(Identity, [Endpoint], Settings);
        Assert.Equal(3, policy.Spec.Egress.Count);
        Assert.Empty(policy.Spec.Ingress);
        Assert.Equal(
            Identity.GatewayId,
            policy.Spec.PodSelector.MatchLabels[PrivateAccessGatewayManifest.GatewayLabel]);
        var destination = Assert.Single(policy.Spec.Egress[0].To);
        Assert.Equal(
            Endpoint.KubernetesNamespace,
            destination.NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Equal(
            Identity.TenantId,
            destination.NamespaceSelector.MatchLabels[PrivateAccessGatewayManifest.TenantLabel]);
        Assert.Equal(
            Endpoint.ResourceDeploymentId,
            destination.PodSelector.MatchLabels[PrivateAccessGatewayManifest.DeploymentLabel]);
        Assert.Equal("5432", Assert.Single(policy.Spec.Egress[0].Ports).Port.ToString());
        Assert.Equal("192.0.2.8/32", Assert.Single(policy.Spec.Egress[1].To).IpBlock.Cidr);
        Assert.Equal("9444", Assert.Single(policy.Spec.Egress[1].Ports).Port.ToString());
        Assert.Equal(
            "kube-system",
            Assert.Single(policy.Spec.Egress[2].To).NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Equal(2, policy.Spec.Egress[2].Ports.Count);
    }

    [Fact]
    public void Egress_RejectsMissingBrokerAddresses()
    {
        var invalid = Settings with { BrokerAddresses = [] };
        Assert.Throws<ArgumentException>(() =>
            PrivateAccessGatewayManifest.BuildGatewayEgress(Identity, [Endpoint], invalid));
    }

    [Fact]
    public void DestinationIngress_SelectsExactGatewayAndProviderDeployment()
    {
        var policy = PrivateAccessGatewayManifest.BuildDestinationIngress(Identity, Endpoint);
        Assert.Equal(Endpoint.KubernetesNamespace, policy.Metadata.NamespaceProperty);
        Assert.Equal(
            Endpoint.ResourceDeploymentId,
            policy.Spec.PodSelector.MatchLabels[PrivateAccessGatewayManifest.DeploymentLabel]);
        var rule = Assert.Single(policy.Spec.Ingress);
        Assert.Equal("5432", Assert.Single(rule.Ports).Port.ToString());
        var peer = Assert.Single(rule.FromProperty);
        Assert.Equal(
            PrivateAccessGatewayManifest.Namespace(Identity),
            peer.NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Equal(Identity.GatewayId, peer.PodSelector.MatchLabels[PrivateAccessGatewayManifest.GatewayLabel]);
    }
}
