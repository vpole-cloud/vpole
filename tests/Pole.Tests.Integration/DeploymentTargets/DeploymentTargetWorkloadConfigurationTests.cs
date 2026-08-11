using MongoDB.Bson;
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
            CreatedAtUtc.AddMinutes(1));

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
        return DeploymentTargetFactory.Create(
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
