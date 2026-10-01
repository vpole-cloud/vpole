using System.Net;
using Pole.Features.PrivateAccess.Runtime;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.PrivateAccess.Tests;

public sealed class PrivateAccessWorkerManifestTests
{
    [Fact]
    public void WorkerNamespaceBaseline_DeniesAllPods_IndependentlyOfSessionRules()
    {
        var baseline = PrivateAccessWorkerManifest.BuildWorkerBaseline("target-a", "workers");
        Assert.Equal(PrivateAccessWorkerManifest.WorkerBaselineName, baseline.Metadata.Name);
        Assert.Null(baseline.Spec.PodSelector.MatchLabels);
        Assert.Null(baseline.Spec.PodSelector.MatchExpressions);
        Assert.Equal(["Ingress", "Egress"], baseline.Spec.PolicyTypes);
        Assert.Empty(baseline.Spec.Ingress);
        Assert.Empty(baseline.Spec.Egress);
        Assert.False(baseline.Metadata.Labels.ContainsKey(PrivateAccessWorkerManifest.SessionLabel));
    }

    private static readonly PrivateAccessWorkerDestination Destination = new(
        "tenant-a", "target-a", "cell-a", "deployment-a", "runtime-a", "database",
        NetworkPort.Create(5432), NetworkPort.Create(5432));
    private static readonly PrivateAccessWorkerSettings Settings = new(
        "pole-private-access", "pole/connector:test", new Uri("https://broker.example:443"),
        [IPAddress.Parse("192.0.2.8")]);

    [Fact]
    public void Worker_HasNoClusterCredentialsOrHostPrivileges()
    {
        var job = PrivateAccessWorkerManifest.BuildJob("session-a", Destination, Settings,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var pod = job.Spec.Template.Spec;
        Assert.False(pod.AutomountServiceAccountToken);
        Assert.False(pod.HostNetwork);
        Assert.False(pod.HostPID);
        Assert.False(pod.HostIPC);
        Assert.True(pod.SecurityContext.RunAsNonRoot);
        Assert.Equal("RuntimeDefault", pod.SecurityContext.SeccompProfile.Type);
        var container = Assert.Single(pod.Containers);
        Assert.False(container.SecurityContext.AllowPrivilegeEscalation);
        Assert.True(container.SecurityContext.ReadOnlyRootFilesystem);
        Assert.Equal(["ALL"], container.SecurityContext.Capabilities.Drop);
        Assert.DoesNotContain(container.Env, item => item.Name.Contains("TOKEN", StringComparison.Ordinal));
        Assert.Equal("pole-private-access", job.Metadata.NamespaceProperty);
        Assert.NotEqual(Destination.KubernetesNamespace, job.Metadata.NamespaceProperty);
        Assert.Equal(0, job.Spec.BackoffLimit);
    }

    [Fact]
    public void WorkerEgress_IsOneDeploymentPortAndExactBrokerAddress()
    {
        var policy = PrivateAccessWorkerManifest.BuildWorkerPolicy("session-a", Destination, Settings,
            DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(["Ingress", "Egress"], policy.Spec.PolicyTypes);
        Assert.Empty(policy.Spec.Ingress);
        Assert.Equal(3, policy.Spec.Egress.Count);
        var destination = Assert.Single(policy.Spec.Egress[0].To);
        Assert.Equal("runtime-a", destination.NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Equal("deployment-a", destination.PodSelector.MatchLabels[PrivateAccessWorkerManifest.DeploymentLabel]);
        Assert.Equal("5432", Assert.Single(policy.Spec.Egress[0].Ports).Port.ToString());
        var broker = Assert.Single(policy.Spec.Egress[1].To);
        Assert.Equal("192.0.2.8/32", broker.IpBlock.Cidr);
        Assert.Equal("443", Assert.Single(policy.Spec.Egress[1].Ports).Port.ToString());
        Assert.Equal("kube-system", Assert.Single(policy.Spec.Egress[2].To)
            .NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
    }

    [Fact]
    public void DestinationIngress_SelectsExactSessionAndWorkerNamespaceTogether()
    {
        var policy = PrivateAccessWorkerManifest.BuildDestinationPolicy("session-a", Destination, Settings,
            DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal("runtime-a", policy.Metadata.NamespaceProperty);
        Assert.Equal("deployment-a", policy.Spec.PodSelector.MatchLabels[PrivateAccessWorkerManifest.DeploymentLabel]);
        var ingress = Assert.Single(policy.Spec.Ingress);
        var peer = Assert.Single(ingress.FromProperty);
        Assert.Equal("pole-private-access", peer.NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Equal("target-a", peer.NamespaceSelector.MatchLabels[PrivateAccessWorkerManifest.TargetLabel]);
        Assert.Equal("session-a", peer.PodSelector.MatchLabels[PrivateAccessWorkerManifest.SessionLabel]);
        Assert.Equal("tenant-a", peer.PodSelector.MatchLabels[PrivateAccessWorkerManifest.TenantLabel]);
        Assert.Equal("5432", Assert.Single(ingress.Ports).Port.ToString());
    }
}
