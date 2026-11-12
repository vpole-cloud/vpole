using System.Text.Json;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Core.Kubernetes;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Contracts;
using Xunit;
using PostgresCluster = Pole.Features.Postgres.Domain.PostgresCluster;

namespace Pole.Features.Postgres.Tests;

[Trait("Layer", "Manifest")]
[Trait("Concern", "Rendering")]
[Trait("Scope", "ManifestOnly")]
[Trait("Feature", "Postgres")]
public sealed class PostgresManifestTests
{
    private const string ProjectionName = "pole-secret-projection-deployment-a";

    [Fact]
    public void PreparedClustersCannotProduceOperatorManifests()
    {
        var prepared = PostgresProvisioningTests.Prepared();
        Assert.Throws<InvalidOperationException>(() => PostgresClusterKubernetesBuilder.buildCluster(
            prepared,
            Projection(),
            "pole-data",
            "fast-db",
            MinimalCompute(),
            Metadata(),
            CreateSettings()));
    }

    [Fact]
    public void BuildCluster_UsesNamespaceSpecAndSecretReference()
    {
        var (cluster, settings) = CreateOperatorSubject();
        var placementMetadata = Metadata();
        var clusterManifest = JsonSerializer.SerializeToNode(
            PostgresClusterKubernetesBuilder.buildCluster(
                cluster,
                Projection(),
                "pole-data",
                "fast-db",
                Compute(),
                placementMetadata,
                settings))!.AsObject();
        var metadata = clusterManifest["metadata"]!.AsObject();
        var spec = clusterManifest["spec"]!.AsObject();
        Assert.True(spec["enableSuperuserAccess"]!.GetValue<bool>());
        Assert.Null(spec["bootstrap"]);
        var inheritedMetadata = spec["inheritedMetadata"]!.AsObject();
        var storage = spec["storage"]!.AsObject();
        var resources = spec["resources"]!.AsObject();
        var requests = resources["requests"]!.AsObject();
        var limits = resources["limits"]!.AsObject();
        var secret = spec["superuserSecret"]!.AsObject();

        Assert.Equal("Cluster", clusterManifest["kind"]!.GetValue<string>());
        Assert.Equal(
            PostgresClusterKubernetesBuilder.buildResourceName(cluster.Id),
            metadata["name"]!.GetValue<string>());
        Assert.Equal("pole-data", metadata["namespace"]!.GetValue<string>());
        Assert.Equal("Postgres", metadata["labels"]!["pole.io/resource-type"]!.GetValue<string>());
        Assert.Equal("pole/postgresql", metadata["annotations"]!["pole.io/resource-type"]!.GetValue<string>());
        Assert.Equal("Postgres", inheritedMetadata["labels"]!["pole.io/resource-type"]!.GetValue<string>());
        Assert.Equal(TestData.TenantId, inheritedMetadata["labels"]!["pole.io/tenant-id"]!.GetValue<string>());
        Assert.Equal(
            placementMetadata.RuntimeCellId,
            inheritedMetadata["labels"]!["pole.io/runtime-cell-id"]!.GetValue<string>());
        Assert.Equal(
            placementMetadata.DeploymentId,
            inheritedMetadata["labels"]!["pole.io/resource-deployment-id"]!.GetValue<string>());
        Assert.Equal("pole/postgresql", inheritedMetadata["annotations"]!["pole.io/resource-type"]!.GetValue<string>());
        Assert.Equal(2, spec["instances"]!.GetValue<int>());

        Assert.Equal("20Gi", storage["size"]!.GetValue<string>());
        Assert.Equal("fast-db", storage["storageClass"]!.GetValue<string>());
        Assert.Equal("750m", requests["cpu"]!.GetValue<string>());
        Assert.Equal("768Mi", requests["memory"]!.GetValue<string>());
        Assert.Equal("2", limits["cpu"]!.GetValue<string>());
        Assert.Equal("2Gi", limits["memory"]!.GetValue<string>());
        Assert.Equal("1", requests["nvidia.com/gpu"]!.GetValue<string>());
        var affinity = spec["affinity"]!.AsObject();
        Assert.Equal("linux", affinity["nodeSelector"]!["kubernetes.io/os"]!.GetValue<string>());
        Assert.Equal("arm64", affinity["nodeSelector"]!["kubernetes.io/arch"]!.GetValue<string>());
        var nodeAffinity = affinity["nodeAffinity"]!.AsObject();
        Assert.NotNull(nodeAffinity["requiredDuringSchedulingIgnoredDuringExecution"]);
        Assert.NotNull(nodeAffinity["preferredDuringSchedulingIgnoredDuringExecution"]);
        var toleration = Assert.Single(affinity["tolerations"]!.AsArray())!.AsObject();
        Assert.Equal("dedicated", toleration["key"]!.GetValue<string>());
        Assert.Equal("database", toleration["value"]!.GetValue<string>());
        Assert.Equal("NoSchedule", toleration["effect"]!.GetValue<string>());
        Assert.Equal(ProjectionName, secret["name"]!.GetValue<string>());
        Assert.Equal("ghcr.io/cloudnative-pg/postgresql:17", spec["imageName"]!.GetValue<string>());
        Assert.Equal(
            TestData.DefaultDeploymentTargetId,
            metadata["labels"]!["pole.io/deployment-target-id"]!.GetValue<string>());
    }

    [Fact]
    public void WorkloadManifestsContainOnlyProviderProjectionReferences()
    {
        var (cluster, settings) = CreateOperatorSubject();
        var manifest = PostgresClusterKubernetesBuilder.buildCluster(
            cluster,
            Projection(),
            "pole-data",
            "fast-db",
            MinimalCompute(),
            Metadata(),
            settings);
        var json = JsonSerializer.Serialize(manifest);
        Assert.Contains(ProjectionName, json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(SecretProjectionReference).GetProperties(),
            property => property.Name == "Password");
    }

    [Fact]
    public void BuildCluster_WithoutScheduling_OmitsAffinity()
    {
        var (cluster, settings) = CreateOperatorSubject();

        var manifest = JsonSerializer.SerializeToNode(
            PostgresClusterKubernetesBuilder.buildCluster(
                cluster,
                Projection(),
                "pole-data",
                "fast-db",
                MinimalCompute(),
                Metadata(),
                settings))!.AsObject();

        Assert.False(manifest["spec"]!.AsObject().ContainsKey("affinity"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestoredCluster_BsonRoundTripAndCapacityReconciliationPreserveExactTarget(bool byTime)
    {
        var (source, settings) = CreateOperatorSubject();
        var target = DateTime.Parse("2026-11-12T17:24:15.123456Z").ToUniversalTime();
        var origin = new PostgresRecoveryOrigin(
            "operation",
            "archive",
            "original-server",
            byTime ? null : "20261112T131500",
            byTime ? target : null);
        var spec = new PostgresClusterSpec(
            source.Spec.Instances,
            source.Spec.Storage,
            source.Spec.StorageProfileId,
            source.Spec.Version,
            origin);
        var cluster = new PostgresCluster(
            source.Id,
            source.TenantId,
            source.Name,
            source.GroupId,
            spec,
            source.Revision,
            source.Lifecycle,
            source.Provisioning,
            source.PlanningFence);
        var restored = BsonSerializer.Deserialize<PostgresCluster>(cluster.ToBsonDocument()).UpdateDesiredState(3, 40);
        Assert.Equal(origin.OperationId, restored.Spec.Recovery.OperationId);
        Assert.Equal(origin.TargetTimeUtc, restored.Spec.Recovery.TargetTimeUtc);
        var specNode = JsonSerializer.SerializeToNode(
            PostgresClusterKubernetesBuilder.buildCluster(
                restored,
                Projection(),
                "pole-data",
                "fast-db",
                MinimalCompute(),
                Metadata(),
                settings))!["spec"]!;
        var recovery = specNode["bootstrap"]!["recovery"]!;
        Assert.Equal("origin", recovery["source"]!.GetValue<string>());
        var selection = recovery["recoveryTarget"]!.AsObject();
        if (byTime)
        {
            Assert.Single(selection);
            Assert.Equal(target.ToString("O"), selection["targetTime"]!.GetValue<string>());
        }
        else
        {
            Assert.Equal("20261112T131500", selection["backupID"]!.GetValue<string>());
            Assert.True(selection["targetImmediate"]!.GetValue<bool>());
            Assert.Null(selection["targetTime"]);
        }

        var parameters = specNode["externalClusters"]![0]!["plugin"]!["parameters"]!;
        Assert.Equal("original-server", parameters["serverName"]!.GetValue<string>());
        Assert.Equal("archive-archive", parameters["barmanObjectName"]!.GetValue<string>());
        Assert.True(specNode["enableSuperuserAccess"]!.GetValue<bool>());
        Assert.Equal(ProjectionName, specNode["superuserSecret"]!["name"]!.GetValue<string>());
        Assert.Null(specNode["plugins"]);
    }

    [Theory]
    [InlineData("0123456789abcdef01234567", "pg-0123456789abcdef01234567")]
    [InlineData("abcdef0123456789abcdef01", "pg-abcdef0123456789abcdef01")]
    public void ResourceName_IsStableAndDns1035Compatible(string clusterId, string expected)
    {
        var actual = PostgresClusterKubernetesBuilder.buildResourceName(clusterId);

        Assert.Equal(expected, actual);
        Assert.Matches("^[a-z]([-a-z0-9]*[a-z0-9])?$", actual);
        Assert.InRange(actual.Length, 1, 63);
    }

    private static SecretProjectionReference Projection() => new("managed-secret", ProjectionName, 7);

    private static (PostgresCluster cluster, PostgresSettings settings) CreateOperatorSubject()
    {
        var cluster = PostgresCluster.Create(
            TestData.TenantId,
            "analytics",
            ObjectId.GenerateNewId().ToString(),
            17,
            TestData.DefaultStorageProfileId,
            new CreatePostgresClusterOptions(FSharpOption<string>.Some("0123456789abcdef01234567"), 2, 20));
        return (cluster, CreateSettings());
    }

    private static KubernetesComputeConfiguration Compute(string? runtimeClassName = null)
    {
        var requiredTerm = new ResourceDeploymentNodeAffinityTerm(
        [
            new ResourceDeploymentNodeAffinityExpression(
                "topology.kubernetes.io/zone",
                ComputeNodeAffinityOperator.In,
                ["ua-1"]),
        ]);
        var deploymentCompute = new ResourceDeploymentComputeConfiguration(
            TestData.DefaultComputeProfileId,
            3,
            TestData.DefaultDeploymentTargetId,
            "PostgreSQL compute",
            new ResourceDeploymentComputeResourceValues(750, 768),
            new ResourceDeploymentComputeResourceValues(2000, 2048),
            ComputeArchitecture.Arm64,
            [new ResourceDeploymentExtendedResource("nvidia.com/gpu", 1)],
            [new ResourceDeploymentNodeSelector("kubernetes.io/os", "linux")],
            [requiredTerm],
            [new ResourceDeploymentPreferredNodeAffinityTerm(50, requiredTerm)],
            [
                new ResourceDeploymentToleration(
                    "dedicated",
                    ComputeTolerationOperator.Equal,
                    "database",
                    ComputeTaintEffect.NoSchedule,
                    null),
            ],
            runtimeClassName);
        return PostgresResourceDeploymentMappings.kubernetesCompute(deploymentCompute);
    }

    private static KubernetesComputeConfiguration MinimalCompute() =>
        PostgresResourceDeploymentMappings.kubernetesCompute(
            new ResourceDeploymentComputeConfiguration(
                TestData.DefaultComputeProfileId,
                1,
                TestData.DefaultDeploymentTargetId,
                "Default",
                new ResourceDeploymentComputeResourceValues(250, 256),
                new ResourceDeploymentComputeResourceValues(1000, 1024),
                ComputeArchitecture.Any,
                [],
                [],
                [],
                [],
                [],
                null));

    private static PostgresSettings CreateSettings() => new()
    {
        OperatorImage = new PostgresImageSettings { Server = "ghcr.io", Name = "cloudnative-pg/postgresql" },
    };

    private static PostgresResourceDeploymentMetadata Metadata() => new(
        "507f1f77bcf86cd799439044",
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        TestData.DefaultDeploymentTargetId,
        7);
}
