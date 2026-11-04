using MongoDB.Bson;
using System.Collections.Immutable;
using Pole.Features.DeploymentTargets.Domain;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

public sealed class DeploymentTargetWorkloadConfigurationTests
{
    private static readonly DateTime CreatedAtUtc =
        new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Reconfigure_IngressChangeAdvancesAggregateAndWorkloadConfigurationRevisions()
    {
        var target = CreateTarget();
        var replacement = DeploymentTargetIngressConfiguration.Create(
            "nginx",
            "private-ca");

        var updated = target.Reconfigure(
            target.DisplayName,
            target.Enabled,
            target.AcceptsWorkloads,
            replacement,
            target.WorkloadConfiguration.NetworkSources,
            CreatedAtUtc.AddMinutes(1));

        Assert.Equal(target.Revision + 1, updated.Revision);
        Assert.Equal(
            target.WorkloadConfiguration.Revision + 1,
            updated.WorkloadConfiguration.Revision);
        Assert.Equal(replacement, updated.WorkloadConfiguration.Ingress);
    }

    [Fact]
    public void Reconfigure_MetadataChangePreservesWorkloadConfigurationRevision()
    {
        var target = CreateTarget();

        var updated = target.Reconfigure(
            "Renamed target",
            target.Enabled,
            target.AcceptsWorkloads,
            target.WorkloadConfiguration.Ingress,
            target.WorkloadConfiguration.NetworkSources,
            CreatedAtUtc.AddMinutes(1));

        Assert.Equal(target.Revision + 1, updated.Revision);
        Assert.Equal(
            target.WorkloadConfiguration.Revision,
            updated.WorkloadConfiguration.Revision);
        Assert.Equal(
            target.WorkloadConfiguration.Ingress,
            updated.WorkloadConfiguration.Ingress);
    }

    [Fact]
    public void Reconfigure_NoOpPreservesBothRevisions()
    {
        var target = CreateTarget();

        var unchanged = target.Reconfigure(
            target.DisplayName,
            target.Enabled,
            target.AcceptsWorkloads,
            target.WorkloadConfiguration.Ingress,
            target.WorkloadConfiguration.NetworkSources,
            CreatedAtUtc.AddMinutes(1));

        Assert.Equal(target.Revision, unchanged.Revision);
        Assert.Equal(
            target.WorkloadConfiguration.Revision,
            unchanged.WorkloadConfiguration.Revision);
        Assert.Equal(target.UpdatedAtUtc, unchanged.UpdatedAtUtc);
    }

    [Fact]
    public void Reconfigure_RemovingIngressAdvancesBothRevisions()
    {
        var target = CreateTarget();

        var updated = target.Reconfigure(
            target.DisplayName,
            target.Enabled,
            target.AcceptsWorkloads,
            ingress: null,
            networkSources: target.WorkloadConfiguration.NetworkSources,
            updatedAtUtc: CreatedAtUtc.AddMinutes(1));

        Assert.Equal(target.Revision + 1, updated.Revision);
        Assert.Equal(
            target.WorkloadConfiguration.Revision + 1,
            updated.WorkloadConfiguration.Revision);
        Assert.Null(updated.WorkloadConfiguration.Ingress);
    }

    [Fact]
    public void ReplaceConnectionCredential_PreservesWorkloadConfigurationRevision()
    {
        var target = CreateTarget();
        var replacement = DeploymentTargetConnectionFactory.CreateInCluster(
            target.NextConnectionVersion);

        var updated = target.ReplaceConnectionCredential(
            target.ClusterIdentity,
            replacement,
            CreatedAtUtc.AddMinutes(1));

        Assert.Equal(target.Revision + 1, updated.Revision);
        Assert.Equal(
            target.WorkloadConfiguration.Revision,
            updated.WorkloadConfiguration.Revision);
        Assert.Equal(
            target.WorkloadConfiguration.Ingress,
            updated.WorkloadConfiguration.Ingress);
    }

    [Fact]
    public void Reconfigure_NetworkSourceChangeAdvancesConfigurationRevision_AndEquivalentSelectorsDoNot()
    {
        var target = CreateTarget();
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/name"] = "traefik",
            ["app.kubernetes.io/component"] = "controller"
        };
        var source = DeploymentTargetPodNetworkSource.Create("ingress-system", labels);
        var configured = target.Reconfigure(
            target.DisplayName,
            target.Enabled,
            target.AcceptsWorkloads,
            target.WorkloadConfiguration.Ingress,
            new DeploymentTargetNetworkSources(
                ImmutableDictionary<string, DeploymentTargetNetworkSource>.Empty.Add("public-ingress", source)),
            CreatedAtUtc.AddMinutes(1));

        labels["app.kubernetes.io/name"] = "changed-after-factory";
        Assert.Equal("traefik", source.PodLabels["app.kubernetes.io/name"]);
        Assert.Equal(target.WorkloadConfiguration.Revision + 1, configured.WorkloadConfiguration.Revision);

        var equivalent = DeploymentTargetPodNetworkSource.Create("ingress-system", new Dictionary<string, string>
        {
            ["app.kubernetes.io/component"] = "controller",
            ["app.kubernetes.io/name"] = "traefik"
        });
        var unchanged = configured.Reconfigure(
            configured.DisplayName,
            configured.Enabled,
            configured.AcceptsWorkloads,
            configured.WorkloadConfiguration.Ingress,
            new DeploymentTargetNetworkSources(
                ImmutableDictionary<string, DeploymentTargetNetworkSource>.Empty.Add("public-ingress", equivalent)),
            CreatedAtUtc.AddMinutes(2));

        Assert.Equal(configured.Revision, unchanged.Revision);
        Assert.Equal(configured.WorkloadConfiguration.Revision, unchanged.WorkloadConfiguration.Revision);
    }

    [Fact]
    public void Create_RejectsInvalidSourceNameBeforePersistence()
    {
        var source = DeploymentTargetCidrNetworkSource.Create("192.0.2.9/32");
        var sources = new DeploymentTargetNetworkSources(
            ImmutableDictionary<string, DeploymentTargetNetworkSource>.Empty.Add("bad//name", source));

        Assert.Throws<ArgumentException>(() =>
            DeploymentTargetWorkloadConfiguration.Create(null, sources));
    }

    [Fact]
    public void NetworkSources_RejectBroadOrMalformedIdentities()
    {
        Assert.Throws<ArgumentException>(() => DeploymentTargetPodNetworkSource.Create(
            "ingress-system", new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => DeploymentTargetPodNetworkSource.Create(
            "bad.namespace", new Dictionary<string, string> { ["app"] = "controller" }));
        Assert.Throws<ArgumentException>(() => DeploymentTargetPodNetworkSource.Create(
            "ingress-system", new Dictionary<string, string> { ["bad key"] = "controller" }));
        Assert.Throws<ArgumentException>(() => DeploymentTargetPodNetworkSource.Create(
            "ingress-system", new Dictionary<string, string> { ["app"] = " padded " }));
        Assert.Throws<ArgumentException>(() => DeploymentTargetCidrNetworkSource.Create("0.0.0.0/0"));
        Assert.Equal("2001:db8::1/128", DeploymentTargetCidrNetworkSource.Create("2001:db8::1/128").Cidr);
    }

    [Theory]
    [InlineData("UPPERCASE")]
    [InlineData("invalid_name")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("two..labels")]
    public void IngressConfigurationFactory_RejectsInvalidKubernetesNames(string value)
    {
        Assert.Throws<ArgumentException>(
            () => DeploymentTargetIngressConfiguration.Create(
                value,
                "letsencrypt-prod"));
    }

    [Fact]
    public void IngressConfigurationFactory_NormalizesPublicBaseDomainToCanonicalAscii()
    {
        var ingress = DeploymentTargetIngressConfiguration.Create(
            "traefik",
            "letsencrypt-prod",
            "Store.Example.TEST.");

        Assert.Equal("store.example.test", ingress.PublicBaseDomain);
    }

    [Theory]
    [InlineData("https://example.test")]
    [InlineData("example.test:443")]
    [InlineData("*.example.test")]
    [InlineData("127.0.0.1")]
    [InlineData("привіт.example.test")]
    public void IngressConfigurationFactory_RejectsNonDnsPublicBaseDomain(string publicBaseDomain)
    {
        Assert.Throws<ArgumentException>(
            () => DeploymentTargetIngressConfiguration.Create(
                "traefik",
                "letsencrypt-prod",
                publicBaseDomain));
    }

    private static DeploymentTarget CreateTarget()
    {
        var id = ObjectId.GenerateNewId().ToString();
        const long connectionVersion = 1;
        return DeploymentTargetFactory.Create("target-" + id,
            "Workload configuration target",
            $"kubernetes:kube-system:{id}",
            DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
            DeploymentTargetHealth.Healthy(connectionVersion, CreatedAtUtc),
            DeploymentTargetCapabilities.Unknown(connectionVersion),
            DeploymentTargetTestData.CreateWorkloadConfiguration(),
            enabled: true,
            acceptsWorkloads: true,
            new DeploymentTargetFactory.CreateOptions(id, CreatedAtUtc));
    }
}
