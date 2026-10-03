using k8s;
using k8s.Autorest;
using System.Net;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using Npgsql;
using Pole.Features.Core.Kubernetes;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Shared.Models;
using Pole.Features.Postgres.Tests.Integration.Infrastructure;
using Pole.Testing;
using Xunit;
using PostgresCluster = Pole.Features.Postgres.Domain.PostgresCluster;

namespace Pole.Features.Postgres.Tests.Integration;

[Trait("Layer", "Kubernetes")]
[Trait("Concern", "Feature")]
[Trait("Scope", "SingleCluster")]
[Trait("Feature", "Postgres")]
public sealed class PostgresKubernetesTests(KubernetesClusterFixture fixture)
    : KubernetesIntegrationTestBase(fixture)
{
    [KubernetesFact("cnpg")]
    public async Task OperatorCreateAsync_ConsumesManagedProjectionAndCreatesClusterCustomResource()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterDeploymentManager manager =
            new PostgresClusterDeploymentManager(CreateSettings());
        using var projection = new ManagedProjectionFixture(Kubernetes, cluster, NamespaceName);
        await projection.ApplyAsync("initial-secret");

        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            projection.Credentials,
            NamespaceName,
            Target.StorageClassName,
            Compute(),
            Metadata(),
            CancellationToken.None);

        var secret = await Kubernetes.CoreV1.ReadNamespacedSecretAsync(
            projection.Projection.KubernetesName,
            NamespaceName);
        var clusterResource = await Kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
            group: "postgresql.cnpg.io",
            version: "v1",
            namespaceParameter: NamespaceName,
            plural: "clusters",
            name: PostgresClusterKubernetesBuilder.buildResourceName(cluster.Id));

        Assert.Equal("postgres-user", System.Text.Encoding.UTF8.GetString(secret.Data["username"]));
        Assert.Equal("initial-secret", System.Text.Encoding.UTF8.GetString(secret.Data["password"]));
        Assert.NotNull(clusterResource);
    }

    [KubernetesFact("cnpg")]
    public async Task OperatorCreateAsync_WaitsForClusterReadiness_AndExecutesSimpleSql()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterDeploymentManager manager =
            new PostgresClusterDeploymentManager(CreateSettings());
        using var projection = new ManagedProjectionFixture(Kubernetes, cluster, NamespaceName);
        await projection.ApplyAsync("initial-secret");

        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            projection.Credentials,
            NamespaceName,
            Target.StorageClassName,
            Compute(),
            Metadata(),
            CancellationToken.None);
        await WaitForClusterReadyAsync(
            PostgresClusterKubernetesBuilder.buildResourceName(cluster.Id),
            expectedReadyInstances: cluster.Spec.Instances);

        var result = await ExecuteSimpleSqlAsync(cluster);

        Assert.Equal(1, result);
    }

    [KubernetesFact("cnpg")]
    public async Task ManagedProjectionRotation_RewritesSecretBeforeWorkloadReapply()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterDeploymentManager manager =
            new PostgresClusterDeploymentManager(CreateSettings());
        using var projection = new ManagedProjectionFixture(Kubernetes, cluster, NamespaceName);
        await projection.ApplyAsync("initial-secret");

        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            projection.Credentials,
            NamespaceName,
            Target.StorageClassName,
            Compute(),
            Metadata(),
            CancellationToken.None);
        await projection.ApplyAsync("rotated-secret");
        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            projection.Credentials,
            NamespaceName,
            Target.StorageClassName,
            Compute(),
            Metadata(),
            CancellationToken.None);

        var secret = await Kubernetes.CoreV1.ReadNamespacedSecretAsync(
            projection.Projection.KubernetesName,
            NamespaceName);

        Assert.Equal("postgres-user", System.Text.Encoding.UTF8.GetString(secret.Data["username"]));
        Assert.Equal("rotated-secret", System.Text.Encoding.UTF8.GetString(secret.Data["password"]));
    }

    [KubernetesFact("cnpg")]
    public async Task OperatorDeleteAsync_LeavesProjectionForItsProviderAndRemovesCluster()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterDeploymentManager manager =
            new PostgresClusterDeploymentManager(CreateSettings());
        using var projection = new ManagedProjectionFixture(Kubernetes, cluster, NamespaceName);
        await projection.ApplyAsync("initial-secret");

        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            projection.Credentials,
            NamespaceName,
            Target.StorageClassName,
            Compute(),
            Metadata(),
            CancellationToken.None);
        await manager.DeleteAsync(
            Kubernetes,
            cluster.Id,
            NamespaceName,
            cluster.Spec.DeploymentMode,
            CancellationToken.None);

        Assert.NotNull(await Kubernetes.CoreV1.ReadNamespacedSecretAsync(projection.Projection.KubernetesName, NamespaceName));
        await projection.ReleaseAsync();
        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedSecretAsync(
            projection.Projection.KubernetesName,
            NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
            group: "postgresql.cnpg.io",
            version: "v1",
            namespaceParameter: NamespaceName,
            plural: "clusters",
            name: PostgresClusterKubernetesBuilder.buildResourceName(cluster.Id)));
    }

    [KubernetesFact]
    public async Task StatefulSetCreateAsync_WaitsForPodReadiness_AndExecutesSimpleSql()
    {
        var cluster = BuildStatefulSetCluster();
        var metadata = Metadata();
        IPostgresClusterDeploymentManager manager =
            new PostgresClusterDeploymentManager(CreateSettings());
        using var projection = new ManagedProjectionFixture(Kubernetes, cluster, NamespaceName);
        await projection.ApplyAsync("initial-secret");

        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            projection.Credentials,
            NamespaceName,
            Target.StorageClassName,
            Compute(),
            metadata,
            CancellationToken.None);
        await WaitForStatefulSetReadyAsync(
            PostgresClusterKubernetesBuilder.buildResourceName(cluster.Id));

        var result = await ExecuteSimpleSqlAsync(cluster);

        Assert.Equal(1, result);
        var nextMetadata = new PostgresResourceDeploymentMetadata(metadata.DeploymentId,
            metadata.IsolationScopeId, metadata.RuntimeCellId, metadata.DeploymentTargetId, 2);
        var nextCompute = Compute() with { Requests = new KubernetesComputeResourceValues(500, 256) };
        await manager.ApplyAsync(Kubernetes, cluster, projection.Credentials, NamespaceName,
            Target.StorageClassName, nextCompute, nextMetadata, CancellationToken.None);
        var reapplied = await Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(
            PostgresClusterKubernetesBuilder.buildResourceName(cluster.Id), NamespaceName);
        Assert.Equal("2", reapplied.Spec.Template.Metadata.Labels["pole.io/deployment-generation"]);
        Assert.Equal("1", Assert.Single(reapplied.Spec.VolumeClaimTemplates).Metadata.Labels["pole.io/deployment-generation"]);
        Assert.Equal("500m", Assert.Single(reapplied.Spec.Template.Spec.Containers).Resources.Requests["cpu"].ToString());
        var legacyResize = PostgresCluster.Create(cluster.TenantId, cluster.Name, cluster.GroupId,
            cluster.Spec.Version, cluster.Spec.DeploymentMode, cluster.Spec.StorageProfileId,
            new CreatePostgresClusterOptions(FSharpOption<string>.Some(cluster.Id), 1, cluster.Spec.Storage + 1));
        await Assert.ThrowsAsync<HttpOperationException>(() => manager.ApplyAsync(Kubernetes, legacyResize,
            projection.Credentials, NamespaceName, Target.StorageClassName, nextCompute, nextMetadata, CancellationToken.None));
    }

    [KubernetesFact]
    public async Task StatefulSetDeleteAsync_LeavesProjectionForItsProviderAndRemovesResources()
    {
        var cluster = BuildStatefulSetCluster();
        IPostgresClusterDeploymentManager manager =
            new PostgresClusterDeploymentManager(CreateSettings());
        using var projection = new ManagedProjectionFixture(Kubernetes, cluster, NamespaceName);
        await projection.ApplyAsync("initial-secret");

        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            projection.Credentials,
            NamespaceName,
            Target.StorageClassName,
            Compute(),
            Metadata(),
            CancellationToken.None);
        await manager.DeleteAsync(
            Kubernetes,
            cluster.Id,
            NamespaceName,
            cluster.Spec.DeploymentMode,
            CancellationToken.None);

        Assert.NotNull(await Kubernetes.CoreV1.ReadNamespacedSecretAsync(projection.Projection.KubernetesName, NamespaceName));
        await projection.ReleaseAsync();
        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedSecretAsync(
            projection.Projection.KubernetesName,
            NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(
            PostgresClusterKubernetesBuilder.buildResourceName(cluster.Id),
            NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedServiceAsync(
            PostgresStatefulSetKubernetesBuilder.buildHeadlessServiceName(cluster.Id),
            NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedServiceAsync(
            PostgresClusterKubernetesBuilder.buildReadWriteServiceName(cluster.Id),
            NamespaceName));
    }

    private PostgresCluster BuildOperatorCluster() =>
        BuildCluster(PostgresDeploymentMode.Operator);

    private PostgresCluster BuildStatefulSetCluster() =>
        BuildCluster(PostgresDeploymentMode.StatefulSet);

    private static PostgresCluster BuildCluster(PostgresDeploymentMode mode) =>
        PostgresCluster.Create(
            PostgresTestData.TenantId,
            mode == PostgresDeploymentMode.Operator
                ? "postgres-k8s"
                : "postgres-sts",
            ObjectId.GenerateNewId().ToString(),
            17,
            mode,
            PostgresTestData.DefaultStorageProfileId,
            new CreatePostgresClusterOptions(
                FSharpOption<string>.Some(ObjectId.GenerateNewId().ToString()),
                1,
                5));

    private static PostgresResourceDeploymentMetadata Metadata() =>
        new(
            "507f1f77bcf86cd799439044",
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            PostgresTestData.DefaultDeploymentTargetId,
            1);

    private static KubernetesComputeConfiguration Compute() =>
        new(
            new KubernetesComputeResourceValues(250, 256),
            new KubernetesComputeResourceValues(1000, 1024),
            ComputeArchitecture.Any,
            [],
            [],
            [],
            [],
            [],
            null);

    private static PostgresSettings CreateSettings() =>
        new()
        {
            OperatorImage = new PostgresImageSettings { Server = "ghcr.io", Name = "cloudnative-pg/postgresql" },
            StatefulSetImage = new PostgresImageSettings { Server = "docker.io", Name = "postgres" }
        };

    private async Task WaitForClusterReadyAsync(string clusterId, int expectedReadyInstances, int timeoutSeconds = 180)
    {
        var elapsed = Stopwatch.StartNew();
        string? lastStatus = null;

        while (elapsed.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var clusterResource = await Kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
                group: "postgresql.cnpg.io",
                version: "v1",
                namespaceParameter: NamespaceName,
                plural: "clusters",
                name: clusterId);

            var document = JsonSerializer.SerializeToNode(clusterResource)!.AsObject();
            var status = document["status"]?.AsObject();
            lastStatus = status?.ToJsonString();

            var readyInstances = status?["readyInstances"]?.GetValue<int?>() ?? 0;
            if (readyInstances >= expectedReadyInstances)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw await CreateTimeoutExceptionAsync(
            $"Postgres cluster '{clusterId}' did not become ready in namespace '{NamespaceName}'. " +
            $"Last status: {lastStatus ?? "<missing>"}");
    }

    private async Task WaitForStatefulSetReadyAsync(string statefulSetName, int timeoutSeconds = 180)
    {
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            var statefulSet = await Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(statefulSetName, NamespaceName);
            var readyReplicas = statefulSet.Status?.ReadyReplicas ?? 0;
            if (readyReplicas >= 1)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw await CreateTimeoutExceptionAsync(
            $"StatefulSet '{statefulSetName}' did not become ready in namespace '{NamespaceName}'.");
    }

    private async Task<object?> ExecuteSimpleSqlAsync(PostgresCluster cluster, int timeoutSeconds = 60)
    {
        var elapsed = Stopwatch.StartNew();
        Exception? lastError = null;

        while (elapsed.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            try
            {
                await using var portForward = await KubernetesPortForwardSession.StartAsync(
                    Target,
                    NamespaceName,
                    $"service/{PostgresClusterKubernetesBuilder.buildReadWriteServiceName(cluster.Id)}",
                    remotePort: 5432);
                await using var connection = new NpgsqlConnection(
                    $"Host=127.0.0.1;Port={portForward.LocalPort};Database={PostgresClusterKubernetesBuilder.buildDatabaseName(cluster.Id)};Username=postgres-user;Password=initial-secret;SSL Mode=Disable;Timeout=5;Command Timeout=15");
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                return await command.ExecuteScalarAsync();
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw await CreateTimeoutExceptionAsync(
            $"Postgres service for cluster '{cluster.Id}' did not accept SQL connections in namespace '{NamespaceName}'.",
            lastError);
    }

    private static async Task AssertResourceDeletedAsync<T>(Func<Task<T>> readCall, int timeoutSeconds = 30)
    {
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            try
            {
                await readCall();
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new TimeoutException("Resource was not deleted before the timeout elapsed.");
    }

}
