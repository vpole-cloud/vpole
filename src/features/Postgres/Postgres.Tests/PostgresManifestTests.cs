using System.Text.Json;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using Pole.Features.Core.Kubernetes;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Shared.Models;
using Pole.Features.Postgres.Tests;
using Xunit;
using PostgresCluster = Pole.Features.Postgres.Domain.PostgresCluster;

namespace Pole.Features.Postgres.Tests;

[Trait("Layer", "Manifest")]
[Trait("Concern", "Rendering")]
[Trait("Scope", "ManifestOnly")]
[Trait("Feature", "Postgres")]
public sealed class PostgresManifestTests
{
    [Fact]
    public void PreparedClustersCannotProduceOperatorOrStatefulSetManifests()
    {
        var prepared = PostgresProvisioningTests.Prepared();
        Assert.Throws<InvalidOperationException>(() => PostgresClusterKubernetesBuilder.buildCluster(
            prepared, ProjectedCredentials(), "pole-data", "fast-db", MinimalCompute(), Metadata(), CreateSettings()));
        var stateful = PostgresProvisioningTests.Prepared(PostgresDeploymentMode.StatefulSet);
        Assert.Throws<InvalidOperationException>(() => PostgresStatefulSetKubernetesBuilder.buildStatefulSet(
            stateful, Projection(), "pole-data", "fast-db", MinimalCompute(), Metadata(), CreateSettings()));
    }

    [Fact]
    public void BuildCluster_UsesNamespaceSpecAndSecretReference()
    {
        var (cluster, settings) = CreateOperatorSubject();
        var placementMetadata = Metadata();
        var clusterManifest = JsonSerializer.SerializeToNode(
            PostgresClusterKubernetesBuilder.buildCluster(
                cluster,
                ProjectedCredentials(),
                "pole-data",
                "fast-db",
                Compute(),
                placementMetadata,
                settings))!.AsObject();
        var metadata = clusterManifest["metadata"]!.AsObject();
        var spec = clusterManifest["spec"]!.AsObject();
        var inheritedMetadata = spec["inheritedMetadata"]!.AsObject();
        var storage = spec["storage"]!.AsObject();
        var resources = spec["resources"]!.AsObject();
        var requests = resources["requests"]!.AsObject();
        var limits = resources["limits"]!.AsObject();
        var secret = spec["secret"]!.AsObject();

        Assert.Equal("Cluster", clusterManifest["kind"]!.GetValue<string>());
        Assert.Equal(PostgresClusterKubernetesBuilder.buildResourceName(cluster.Id), metadata["name"]!.GetValue<string>());
        Assert.Equal("pole-data", metadata["namespace"]!.GetValue<string>());
        Assert.Equal(
            "Postgres",
            metadata["labels"]!["pole.io/resource-type"]!.GetValue<string>());
        Assert.Equal(
            "pole/postgresql",
            metadata["annotations"]!["pole.io/resource-type"]!.GetValue<string>());
        Assert.Equal(
            "Postgres",
            inheritedMetadata["labels"]!["pole.io/resource-type"]!.GetValue<string>());
        Assert.Equal(TestData.TenantId, inheritedMetadata["labels"]!["pole.io/tenant-id"]!.GetValue<string>());
        Assert.Equal(placementMetadata.RuntimeCellId, inheritedMetadata["labels"]!["pole.io/runtime-cell-id"]!.GetValue<string>());
        Assert.Equal(placementMetadata.DeploymentId, inheritedMetadata["labels"]!["pole.io/resource-deployment-id"]!.GetValue<string>());
        Assert.Equal(
            "pole/postgresql",
            inheritedMetadata["annotations"]!["pole.io/resource-type"]!.GetValue<string>());
        Assert.Equal(2, spec["instances"]!.GetValue<int>());
        var bootstrap = spec["bootstrap"]!.AsObject()["initdb"]!.AsObject();
        var bootstrapSecret = bootstrap["secret"]!.AsObject();

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
        Assert.Equal(cluster.Id, bootstrap["database"]!.GetValue<string>());
        Assert.Equal("app_user", bootstrap["owner"]!.GetValue<string>());
        Assert.Equal(ProjectionName, bootstrapSecret["name"]!.GetValue<string>());
        Assert.Equal(
            TestData.DefaultDeploymentTargetId,
            metadata["labels"]!["pole.io/deployment-target-id"]!.GetValue<string>());
    }

    [Fact]
    public void WorkloadManifestsContainOnlyProviderProjectionReferences()
    {
        var (cluster, settings) = CreateOperatorSubject();
        var manifest = PostgresClusterKubernetesBuilder.buildCluster(
            cluster, ProjectedCredentials(), "pole-data", "fast-db", MinimalCompute(), Metadata(), settings);
        var json = JsonSerializer.Serialize(manifest);
        Assert.Contains(ProjectionName, json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(typeof(PostgresProjectedCredentials).GetProperties(), property => property.Name == "Password");
    }

    [Fact]
    public void BuildCluster_WithoutScheduling_OmitsAffinity()
    {
        var (cluster, settings) = CreateOperatorSubject();

        var manifest = JsonSerializer.SerializeToNode(
            PostgresClusterKubernetesBuilder.buildCluster(
                cluster,
                ProjectedCredentials(),
                "pole-data",
                "fast-db",
                MinimalCompute(),
                Metadata(),
                settings))!.AsObject();

        Assert.False(manifest["spec"]!.AsObject().ContainsKey("affinity"));
    }

    [Fact]
    public void BuildStatefulSet_UsesConfiguredImageAndSecretBackedEnvironment()
    {
        var (cluster, settings) = CreateStatefulSetSubject();
        var placementMetadata = Metadata();

        var statefulSet = PostgresStatefulSetKubernetesBuilder.buildStatefulSet(
            cluster,
            Projection(),
            "pole-data",
            "fast-db",
            Compute(runtimeClassName: "kata"),
            placementMetadata,
            settings);

        Assert.Equal("apps/v1", statefulSet.ApiVersion);
        Assert.Equal("StatefulSet", statefulSet.Kind);
        Assert.Equal(PostgresClusterKubernetesBuilder.buildResourceName(cluster.Id), statefulSet.Metadata.Name);
        Assert.Equal("pole-data", statefulSet.Metadata.NamespaceProperty);
        Assert.Equal("Postgres", statefulSet.Metadata.Labels["pole.io/resource-type"]);
        Assert.Equal(
            8,
            statefulSet.Spec.Template.Metadata.Labels.Keys.Count(key =>
                key.StartsWith("pole.io/") &&
                key != "pole.io/resource-name" &&
                key != "pole.io/deployment-generation"));
        Assert.Single(statefulSet.Spec.Selector.MatchLabels);
        Assert.Equal(
            statefulSet.Spec.Template.Metadata.Labels["pole.io/resource-deployment-id"],
            statefulSet.Spec.Selector.MatchLabels["pole.io/resource-deployment-id"]);
        Assert.Equal(TestData.TenantId, statefulSet.Spec.Template.Metadata.Labels["pole.io/tenant-id"]);
        Assert.Equal(placementMetadata.RuntimeCellId, statefulSet.Spec.Template.Metadata.Labels["pole.io/runtime-cell-id"]);
        Assert.Equal(placementMetadata.DeploymentId, statefulSet.Spec.Template.Metadata.Labels["pole.io/resource-deployment-id"]);
        Assert.Equal(
            "pole/postgresql",
            statefulSet.Metadata.Annotations["pole.io/resource-type"]);
        Assert.Equal(
            "pole/postgresql",
            statefulSet.Spec.Template.Metadata.Annotations["pole.io/resource-type"]);
        Assert.Equal("docker.io/postgres:17", statefulSet.Spec.Template.Spec.Containers.Single().Image);
        Assert.Equal(PostgresStatefulSetKubernetesBuilder.buildHeadlessServiceName(cluster.Id), statefulSet.Spec.ServiceName);

        var env = statefulSet.Spec.Template.Spec.Containers.Single().Env.ToDictionary(x => x.Name);
        var resources = statefulSet.Spec.Template.Spec.Containers.Single().Resources;
        Assert.Equal("750m", resources.Requests["cpu"].ToString());
        Assert.Equal("768Mi", resources.Requests["memory"].ToString());
        Assert.Equal("2", resources.Limits["cpu"].ToString());
        Assert.Equal("2Gi", resources.Limits["memory"].ToString());
        Assert.Equal("1", resources.Requests["nvidia.com/gpu"].ToString());
        Assert.Equal("linux", statefulSet.Spec.Template.Spec.NodeSelector["kubernetes.io/os"]);
        Assert.Equal("arm64", statefulSet.Spec.Template.Spec.NodeSelector["kubernetes.io/arch"]);
        Assert.Equal("kata", statefulSet.Spec.Template.Spec.RuntimeClassName);
        Assert.NotNull(statefulSet.Spec.Template.Spec.Affinity.NodeAffinity);
        Assert.Single(statefulSet.Spec.Template.Spec.Tolerations);
        Assert.Equal(ProjectionName, env["POSTGRES_USER"].ValueFrom.SecretKeyRef.Name);
        Assert.Equal("username", env["POSTGRES_USER"].ValueFrom.SecretKeyRef.Key);
        Assert.Equal(ProjectionName, env["POSTGRES_PASSWORD"].ValueFrom.SecretKeyRef.Name);
        Assert.Equal("password", env["POSTGRES_PASSWORD"].ValueFrom.SecretKeyRef.Key);
        Assert.Equal(cluster.Id, env["POSTGRES_DB"].Value);
        var volumeClaim = statefulSet.Spec.VolumeClaimTemplates.Single();
        Assert.Equal("20Gi", volumeClaim.Spec.Resources.Requests["storage"].ToString());
        Assert.Equal("fast-db", volumeClaim.Spec.StorageClassName);
        Assert.Equal(TestData.TenantId, volumeClaim.Metadata.Labels["pole.io/tenant-id"]);
        Assert.Equal("Postgres", volumeClaim.Metadata.Labels["pole.io/resource-type"]);
        Assert.Equal(
            "pole/postgresql",
            volumeClaim.Metadata.Annotations["pole.io/resource-type"]);
    }

    [Fact]
    public void BuildStatefulSetServices_UseStableNames()
    {
        var (cluster, _) = CreateStatefulSetSubject();

        var headless = PostgresStatefulSetKubernetesBuilder.buildHeadlessService(
            cluster,
            "pole-data",
            Metadata());
        var readWrite = PostgresStatefulSetKubernetesBuilder.buildReadWriteService(
            cluster,
            "pole-data",
            Metadata());

        Assert.Equal(PostgresStatefulSetKubernetesBuilder.buildHeadlessServiceName(cluster.Id), headless.Metadata.Name);
        Assert.Equal(PostgresClusterKubernetesBuilder.buildReadWriteServiceName(cluster.Id), readWrite.Metadata.Name);
        Assert.Matches("^[a-z]([-a-z0-9]*[a-z0-9])?$", headless.Metadata.Name);
        Assert.Matches("^[a-z]([-a-z0-9]*[a-z0-9])?$", readWrite.Metadata.Name);
        Assert.InRange(headless.Metadata.Name.Length, 1, 63);
        Assert.InRange(readWrite.Metadata.Name.Length, 1, 63);
        Assert.Equal("None", headless.Spec.ClusterIP);
        Assert.Null(readWrite.Spec.ClusterIP);
        Assert.Equal("Postgres", headless.Metadata.Labels["pole.io/resource-type"]);
        Assert.Equal(
            "pole/postgresql",
            headless.Metadata.Annotations["pole.io/resource-type"]);
        Assert.Equal("Postgres", readWrite.Metadata.Labels["pole.io/resource-type"]);
        Assert.Equal(
            "pole/postgresql",
            readWrite.Metadata.Annotations["pole.io/resource-type"]);
    }

    [Theory]
    [InlineData("0123456789abcdef01234567", "pg-0123456789abcdef01234567")]
    [InlineData("abcdef0123456789abcdef01", "pg-abcdef0123456789abcdef01")]
    public void ResourceName_IsStableAndDns1035Compatible(
        string clusterId,
        string expected)
    {
        var actual = PostgresClusterKubernetesBuilder.buildResourceName(clusterId);

        Assert.Equal(expected, actual);
        Assert.Matches("^[a-z]([-a-z0-9]*[a-z0-9])?$", actual);
        Assert.InRange(actual.Length, 1, 63);
    }

    private const string ProjectionName = "pole-secret-projection-deployment-a";
    private static SecretProjectionReference Projection() => new("managed-secret", ProjectionName, 7);
    private static PostgresProjectedCredentials ProjectedCredentials() => PostgresProjectedCredentials.NewOperatorCredentials("app_user", Projection());

    private static (PostgresCluster cluster, PostgresSettings settings) CreateOperatorSubject()
    {
        var cluster = PostgresCluster.Create(
            TestData.TenantId,
            "analytics",
            ObjectId.GenerateNewId().ToString(),
            17,
            PostgresDeploymentMode.Operator,
            TestData.DefaultStorageProfileId,
            new CreatePostgresClusterOptions(
                FSharpOption<string>.Some("0123456789abcdef01234567"),
                2,
                20));
        return (cluster, CreateSettings());
    }

    private static (PostgresCluster cluster, PostgresSettings settings) CreateStatefulSetSubject()
    {
        var cluster = PostgresCluster.Create(
            TestData.TenantId,
            "analytics",
            ObjectId.GenerateNewId().ToString(),
            17,
            PostgresDeploymentMode.StatefulSet,
            TestData.DefaultStorageProfileId,
            new CreatePostgresClusterOptions(
                FSharpOption<string>.Some("0123456789abcdef01234567"),
                1,
                20));
        return (cluster, CreateSettings());
    }

    private static KubernetesComputeConfiguration Compute(string? runtimeClassName = null)
    {
        var requiredTerm = new ResourceDeploymentNodeAffinityTerm(
        [
            new ResourceDeploymentNodeAffinityExpression(
                "topology.kubernetes.io/zone",
                ComputeNodeAffinityOperator.In,
                ["ua-1"])
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
                    null)
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

    private static PostgresSettings CreateSettings() =>
        new()
        {
            OperatorImage = new PostgresImageSettings { Server = "ghcr.io", Name = "cloudnative-pg/postgresql" },
            StatefulSetImage = new PostgresImageSettings { Server = "docker.io", Name = "postgres" }
        };

    private static PostgresResourceDeploymentMetadata Metadata() =>
        new(
            "507f1f77bcf86cd799439044",
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            TestData.DefaultDeploymentTargetId,
            7);
}
