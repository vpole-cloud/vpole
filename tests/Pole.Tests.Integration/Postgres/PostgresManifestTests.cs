using System.Text.Json;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using PostgresCluster = Pole.Features.Postgres.Domain.PostgresCluster;

namespace Pole.Tests.Integration.Postgres;

public sealed class PostgresManifestTests
{
    [Fact]
    public void BuildCluster_UsesNamespaceSpecAndSecretReference()
    {
        var (cluster, settings) = CreateOperatorSubject();
        var clusterManifest = JsonSerializer.SerializeToNode(PostgresClusterKubernetesBuilder.buildCluster(cluster, "app_user", settings))!.AsObject();
        var metadata = clusterManifest["metadata"]!.AsObject();
        var spec = clusterManifest["spec"]!.AsObject();
        var storage = spec["storage"]!.AsObject();
        var secret = spec["secret"]!.AsObject();

        Assert.Equal("Cluster", clusterManifest["kind"]!.GetValue<string>());
        Assert.Equal("cluster-1", metadata["name"]!.GetValue<string>());
        Assert.Equal("pole-data", metadata["namespace"]!.GetValue<string>());
        Assert.Equal(2, spec["instances"]!.GetValue<int>());
        var bootstrap = spec["bootstrap"]!.AsObject()["initdb"]!.AsObject();
        var bootstrapSecret = bootstrap["secret"]!.AsObject();

        Assert.Equal("20Gi", storage["size"]!.GetValue<string>());
        Assert.Equal("fast-db", storage["storageClass"]!.GetValue<string>());
        Assert.Equal("cluster-1-secret", secret["name"]!.GetValue<string>());
        Assert.Equal("ghcr.io/cloudnative-pg/postgresql:17", spec["imageName"]!.GetValue<string>());
        Assert.Equal("cluster-1", bootstrap["database"]!.GetValue<string>());
        Assert.Equal("app_user", bootstrap["owner"]!.GetValue<string>());
        Assert.Equal("cluster-1-secret", bootstrapSecret["name"]!.GetValue<string>());
    }

    [Fact]
    public void BuildSecret_UsesProvidedCredentials()
    {
        var (cluster, _) = CreateOperatorSubject();

        var secret = PostgresClusterKubernetesBuilder.buildSecret(cluster, "app_user", "super-secret");

        Assert.Equal("v1", secret.ApiVersion);
        Assert.Equal("Secret", secret.Kind);
        Assert.Equal("cluster-1-secret", secret.Metadata.Name);
        Assert.Equal("pole-data", secret.Metadata.NamespaceProperty);
        Assert.Equal("kubernetes.io/basic-auth", secret.Type);
        Assert.Equal("app_user", secret.StringData["username"]);
        Assert.Equal("super-secret", secret.StringData["password"]);
    }

    [Fact]
    public void BuildStatefulSet_UsesConfiguredImageAndSecretBackedEnvironment()
    {
        var (cluster, settings) = CreateStatefulSetSubject();

        var statefulSet = PostgresStatefulSetKubernetesBuilder.buildStatefulSet(cluster, settings);

        Assert.Equal("apps/v1", statefulSet.ApiVersion);
        Assert.Equal("StatefulSet", statefulSet.Kind);
        Assert.Equal("cluster-1", statefulSet.Metadata.Name);
        Assert.Equal("pole-data", statefulSet.Metadata.NamespaceProperty);
        Assert.Equal("docker.io/postgres:17", statefulSet.Spec.Template.Spec.Containers.Single().Image);
        Assert.Equal("cluster-1-headless", statefulSet.Spec.ServiceName);

        var env = statefulSet.Spec.Template.Spec.Containers.Single().Env.ToDictionary(x => x.Name);
        Assert.Equal("cluster-1-secret", env["POSTGRES_USER"].ValueFrom.SecretKeyRef.Name);
        Assert.Equal("username", env["POSTGRES_USER"].ValueFrom.SecretKeyRef.Key);
        Assert.Equal("cluster-1-secret", env["POSTGRES_PASSWORD"].ValueFrom.SecretKeyRef.Name);
        Assert.Equal("password", env["POSTGRES_PASSWORD"].ValueFrom.SecretKeyRef.Key);
        Assert.Equal("cluster-1", env["POSTGRES_DB"].Value);
        var volumeClaim = statefulSet.Spec.VolumeClaimTemplates.Single();
        Assert.Equal("20Gi", volumeClaim.Spec.Resources.Requests["storage"].ToString());
        Assert.Equal("fast-db", volumeClaim.Spec.StorageClassName);
        Assert.Equal(TestData.TenantId, volumeClaim.Metadata.Labels["pole.io/tenant-id"]);
    }

    [Fact]
    public void BuildStatefulSetServices_UseStableNames()
    {
        var (cluster, _) = CreateStatefulSetSubject();

        var headless = PostgresStatefulSetKubernetesBuilder.buildHeadlessService(cluster);
        var readWrite = PostgresStatefulSetKubernetesBuilder.buildReadWriteService(cluster);

        Assert.Equal("cluster-1-headless", headless.Metadata.Name);
        Assert.Equal("cluster-1-rw", readWrite.Metadata.Name);
        Assert.Equal("None", headless.Spec.ClusterIP);
        Assert.Null(readWrite.Spec.ClusterIP);
    }

    private static (PostgresCluster cluster, PostgresSettings settings) CreateOperatorSubject()
    {
        var cluster = new PostgresCluster("cluster-1", TestData.TenantId, "analytics", "rg-1", new PostgresClusterSpec(2, 20, TestData.DefaultResolvedStorageProfile("fast-db"), "pole-data", 17, PostgresDeploymentMode.Operator));
        return (cluster, CreateSettings());
    }

    private static (PostgresCluster cluster, PostgresSettings settings) CreateStatefulSetSubject()
    {
        var cluster = new PostgresCluster("cluster-1", TestData.TenantId, "analytics", "rg-1", new PostgresClusterSpec(1, 20, TestData.DefaultResolvedStorageProfile("fast-db"), "pole-data", 17, PostgresDeploymentMode.StatefulSet));
        return (cluster, CreateSettings());
    }

    private static PostgresSettings CreateSettings() =>
        new()
        {
            OperatorImage = new PostgresImageSettings { Server = "ghcr.io", Name = "cloudnative-pg/postgresql" },
            StatefulSetImage = new PostgresImageSettings { Server = "docker.io", Name = "postgres" }
        };
}

