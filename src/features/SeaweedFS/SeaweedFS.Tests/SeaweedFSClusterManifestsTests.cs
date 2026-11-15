using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using k8s;
using Pole.Features.Core.Kubernetes;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.SeaweedFS.Application.Kubernetes;
using Pole.Features.SeaweedFS.Domain;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.SeaweedFS.Tests;

public sealed class SeaweedFSClusterManifestsTests
{
    [Fact]
    public void Single_node_keeps_all_three_disks_and_bootstrap_auth_while_omitting_replication_prerequisites()
    {
        var options = Options(SeaweedFSMode.SingleNode);
        var manifests = SeaweedFSClusterManifests.Build(options);
        var spec = Json(manifests.Seaweed).GetProperty("spec");
        Assert.Null(manifests.MetadataCluster);
        Assert.Null(manifests.RepairScript);
        Assert.Equal("000", spec.GetProperty("master").GetProperty("defaultReplication").GetString());
        foreach (var component in new[] { "master", "volume", "filer", "s3" })
        {
            Assert.Equal(1, spec.GetProperty(component).GetProperty("replicas").GetInt32());
            Assert.Equal(
                "deployment",
                spec.GetProperty(component)
                    .GetProperty("labels")
                    .GetProperty("pole.io/resource-deployment-id")
                    .GetString());
        }

        foreach (var component in new[] { "master", "filer" })
        {
            var persistence = spec.GetProperty(component).GetProperty("persistence");
            Assert.Equal(
                "1Gi",
                persistence.GetProperty("resources").GetProperty("requests").GetProperty("storage").GetString());
            Assert.Equal("fast", persistence.GetProperty("storageClassName").GetString());
            Assert.Equal(
                "deployment",
                persistence.GetProperty("labels").GetProperty("pole.io/resource-deployment-id").GetString());
        }

        Assert.Equal("10Gi", spec.GetProperty("volume").GetProperty("requests").GetProperty("storage").GetString());
        Assert.Equal("bootstrap", spec.GetProperty("s3").GetProperty("configSecret").GetProperty("name").GetString());
        Assert.Equal("filer", spec.GetProperty("filer").GetProperty("configSecret").GetProperty("name").GetString());
    }

    [Fact]
    public void Replicated_mode_has_required_node_separation_synchronous_metadata_and_a_real_repair_job()
    {
        var manifests = SeaweedFSClusterManifests.Build(Options(SeaweedFSMode.Replicated));
        var spec = Json(manifests.Seaweed).GetProperty("spec");
        Assert.Equal("001", spec.GetProperty("master").GetProperty("defaultReplication").GetString());
        foreach (var component in new[] { "master", "volume", "filer", "s3" })
        {
            var value = spec.GetProperty(component);
            Assert.Equal(3, value.GetProperty("replicas").GetInt32());
            var term = value.GetProperty("affinity")
                .GetProperty("podAntiAffinity")
                .GetProperty("requiredDuringSchedulingIgnoredDuringExecution")[0];
            Assert.Equal("kubernetes.io/hostname", term.GetProperty("topologyKey").GetString());
            Assert.Equal(
                "deployment",
                term.GetProperty("labelSelector")
                    .GetProperty("matchLabels")
                    .GetProperty("pole.io/resource-deployment-id")
                    .GetString());
            Assert.Equal(
                component,
                term.GetProperty("labelSelector")
                    .GetProperty("matchLabels")
                    .GetProperty("app.kubernetes.io/component")
                    .GetString());
        }

        Assert.False(spec.GetProperty("filer").TryGetProperty("persistence", out _));
        var metadata = Json(manifests.MetadataCluster!).GetProperty("spec");
        Assert.Equal(3, metadata.GetProperty("instances").GetInt32());
        Assert.Equal(
            "required",
            metadata.GetProperty("postgresql").GetProperty("synchronous").GetProperty("dataDurability").GetString());
        Assert.Equal(1, metadata.GetProperty("postgresql").GetProperty("synchronous").GetProperty("number").GetInt32());
        Assert.True(
            metadata.GetProperty("postgresql").GetProperty("synchronous").GetProperty("failoverQuorum").GetBoolean());
        Assert.Equal(
            "metadata-password",
            metadata.GetProperty("bootstrap")
                .GetProperty("initdb")
                .GetProperty("secret")
                .GetProperty("name")
                .GetString());
        Assert.Equal(
            "deployment",
            metadata.GetProperty("inheritedMetadata")
                .GetProperty("labels")
                .GetProperty("pole.io/resource-deployment-id")
                .GetString());
        var repair = Json(manifests.RepairScript!).GetProperty("spec");
        Assert.Contains("volume.fix.replication -apply", repair.GetProperty("script").GetString());
        Assert.Equal(300, repair.GetProperty("activeDeadlineSeconds").GetInt32());
        Assert.Equal("Forbid", repair.GetProperty("concurrencyPolicy").GetString());
        Assert.Equal(
            "deployment",
            repair.GetProperty("clusterPodLabels").GetProperty("pole.io/resource-deployment-id").GetString());
    }

    [Fact]
    public void Every_workload_preserves_limits_extended_resources_architecture_selectors_and_affinity()
    {
        var manifests = SeaweedFSClusterManifests.Build(Options(SeaweedFSMode.Replicated));
        var spec = Json(manifests.Seaweed).GetProperty("spec");
        foreach (var component in new[] { "master", "volume", "filer", "s3" })
        {
            var value = spec.GetProperty(component);
            Assert.Equal("200m", value.GetProperty("requests").GetProperty("cpu").GetString());
            Assert.Equal("1", value.GetProperty("limits").GetProperty("cpu").GetString());
            Assert.Equal("512Mi", value.GetProperty("limits").GetProperty("memory").GetString());
            Assert.Equal("1", value.GetProperty("requests").GetProperty("example.com/device").GetString());
            Assert.Equal("arm64", value.GetProperty("nodeSelector").GetProperty("kubernetes.io/arch").GetString());
            Assert.Equal("storage", value.GetProperty("nodeSelector").GetProperty("pool").GetString());
            Assert.NotEmpty(
                value.GetProperty("affinity")
                    .GetProperty("nodeAffinity")
                    .GetProperty("requiredDuringSchedulingIgnoredDuringExecution")
                    .GetProperty("nodeSelectorTerms")
                    .EnumerateArray());
            Assert.Single(value.GetProperty("tolerations").EnumerateArray());
        }

        var metadata = Json(manifests.MetadataCluster!).GetProperty("spec");
        Assert.Equal(
            "1",
            metadata.GetProperty("resources").GetProperty("limits").GetProperty("example.com/device").GetString());
        Assert.Equal(
            "arm64",
            metadata.GetProperty("affinity").GetProperty("nodeSelector").GetProperty("kubernetes.io/arch").GetString());
        Assert.Equal(
            "storage",
            Json(manifests.RepairScript!)
                .GetProperty("spec")
                .GetProperty("nodeSelector")
                .GetProperty("pool")
                .GetString());
    }

    [Fact]
    public void Credentials_are_redacted_and_exist_only_in_separate_owned_secret_payloads()
    {
        var options = Options(SeaweedFSMode.Replicated);
        var credentials = new SeaweedFSCredentials("private-access", "private-secret");
        Assert.DoesNotContain("private", credentials.ToString());
        var bootstrap = SeaweedFSClusterManifests.BootstrapSecret(options, credentials);
        var configuration = JsonDocument.Parse(bootstrap.Data[SeaweedFSClusterManifests.BootstrapConfigurationKey])
            .RootElement;
        Assert.Equal(
            "private-secret",
            configuration.GetProperty("identities")[0].GetProperty("credentials")[0]
                .GetProperty("secretKey")
                .GetString());
        Assert.Equal("Admin", configuration.GetProperty("identities")[0].GetProperty("actions")[0].GetString());
        var password = SeaweedFSClusterManifests.MetadataCredentialsSecret(options, "metadata-secret");
        Assert.Equal("kubernetes.io/basic-auth", password.Type);
        Assert.Equal("metadata-secret", Encoding.UTF8.GetString(password.Data["password"]));
        var filer = SeaweedFSClusterManifests.FilerConfigurationSecret(options, "metadata-secret");
        var toml = Encoding.UTF8.GetString(filer.Data[SeaweedFSClusterManifests.FilerConfigurationKey]);
        Assert.Contains("sslmode = \"require\"", toml);
        Assert.Contains("sw-resource-metadata-rw", toml);
        Assert.DoesNotContain("leveldb", toml);
        var bundle = SeaweedFSClusterManifests.Build(options);
        Assert.DoesNotContain("private-secret", KubernetesJson.Serialize(bundle));
        Assert.DoesNotContain("metadata-secret", KubernetesJson.Serialize(bundle));
        Assert.Equal("deployment", filer.Metadata.Labels["pole.io/resource-deployment-id"]);
    }

    [Fact]
    public void Existing_metadata_uses_its_dedicated_login_without_creating_another_database_cluster()
    {
        var options = Options(SeaweedFSMode.Replicated) with
        {
            ExistingMetadata = new SeaweedFSMetadataConnection(
                "tenant-pg-rw.other-cell.svc.cluster.local",
                "owned_metadata",
                "owned_role"),
        };
        var bundle = SeaweedFSClusterManifests.Build(options);
        Assert.Null(bundle.MetadataCluster);
        Assert.NotNull(bundle.RepairScript);
        Assert.Throws<ArgumentException>(() =>
            SeaweedFSClusterManifests.MetadataCredentialsSecret(options, "password"));
        var secret = SeaweedFSClusterManifests.FilerConfigurationSecret(options, "owner-password");
        var configuration = Encoding.UTF8.GetString(secret.Data[SeaweedFSClusterManifests.FilerConfigurationKey]);
        Assert.Contains("tenant-pg-rw.other-cell.svc.cluster.local", configuration);
        Assert.Contains("database = \"owned_metadata\"", configuration);
        Assert.Contains("username = \"owned_role\"", configuration);
        Assert.Contains("owner-password", configuration);
        Assert.DoesNotContain("owner-password", KubernetesJson.Serialize(bundle));
    }

    [Fact]
    public void Volume_allocation_keeps_filesystem_reserve_and_supports_the_sixteen_tebibyte_limit()
    {
        foreach (var sizeGb in new[] { 1, 16 * 1024 })
        {
            var options = Options(SeaweedFSMode.Replicated) with { VolumeSizeGb = sizeGb };
            var spec = Json(SeaweedFSClusterManifests.Build(options).Seaweed).GetProperty("spec");
            Assert.Equal(64, spec.GetProperty("master").GetProperty("volumeSizeLimitMB").GetInt32());
            Assert.Contains("copy_2 = 1", spec.GetProperty("master").GetProperty("config").GetString());
            var count = spec.GetProperty("volume").GetProperty("maxVolumeCounts").GetInt32();
            Assert.Equal(sizeGb * 1024 - 64, count * 64);
            Assert.Equal(1, spec.GetProperty("volume").GetProperty("minFreeSpacePercent").GetInt32());
        }
    }

    [Fact]
    public void Unsupported_runtime_class_is_rejected_instead_of_silently_dropped()
    {
        var options = Options(SeaweedFSMode.SingleNode);
        options = options with { Compute = options.Compute with { RuntimeClassName = "sandbox" } };
        Assert.Throws<ArgumentException>(() => SeaweedFSClusterManifests.Build(options));
    }

    private static JsonElement Json(object value) => JsonDocument.Parse(KubernetesJson.Serialize(value)).RootElement;

    private static SeaweedFSClusterManifestOptions Options(SeaweedFSMode mode) => new(
        "sw-resource",
        "cell",
        mode,
        10,
        new ResourceDeploymentStorageConfiguration("profile", 1, "fast"),
        new ResourceDeploymentComputeConfiguration(
            "profile",
            1,
            "target",
            "Storage",
            new ResourceDeploymentComputeResourceValues(200, 256),
            new ResourceDeploymentComputeResourceValues(1000, 512),
            ComputeArchitecture.Arm64,
            [new ResourceDeploymentExtendedResource("example.com/device", 1)],
            [new ResourceDeploymentNodeSelector("pool", "storage")],
            [
                new ResourceDeploymentNodeAffinityTerm(
                    [new ResourceDeploymentNodeAffinityExpression("disk", ComputeNodeAffinityOperator.In, ["ssd"])])
            ],
            [],
            [
                new ResourceDeploymentToleration(
                    "dedicated",
                    ComputeTolerationOperator.Equal,
                    "storage",
                    ComputeTaintEffect.NoSchedule,
                    null)
            ],
            null),
        new WorkloadIdentityLabels("tenant", "group", "scope", "cell", "target", "SeaweedFS", "resource", "deployment"),
        "bootstrap",
        "filer",
        "metadata-password");
}
