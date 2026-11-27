// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using Npgsql;
using Pole.Features.Core.Kubernetes;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Tests.Kubernetes.Infrastructure;
using Xunit;
using PostgresCluster = Pole.Features.Postgres.Domain.PostgresCluster;

namespace Pole.Features.Postgres.Tests.Kubernetes;

[Trait("Layer", "Kubernetes")]
[Trait("Concern", "Feature")]
[Trait("Scope", "SingleCluster")]
[Trait("Feature", "Postgres")]
public sealed class PostgresKubernetesTests(
    KubernetesClusterFixture fixture
) : KubernetesIntegrationTestBase(fixture)
{
    [KubernetesFact("cnpg")]
    public async Task OperatorCreateAsync_ConsumesManagedProjectionAndCreatesClusterCustomResource()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterDeploymentManager manager = new PostgresClusterDeploymentManager(CreateSettings());
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

        Assert.Equal("postgres", Encoding.UTF8.GetString(secret.Data["username"]));
        Assert.Equal("initial-secret", Encoding.UTF8.GetString(secret.Data["password"]));
        Assert.NotNull(clusterResource);
    }

    [KubernetesFact("cnpg")]
    public async Task OperatorCreateAsync_WaitsForClusterReadiness_AndExecutesSimpleSql()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterDeploymentManager manager = new PostgresClusterDeploymentManager(CreateSettings());
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
        var resources = new TestResourceScope(nameof(ManagedProjectionRotation_RewritesSecretBeforeWorkloadReapply));
        await resources.RunAsync(async () =>
        {
            var cluster = BuildOperatorCluster();
            IPostgresClusterDeploymentManager manager = new PostgresClusterDeploymentManager(CreateSettings());
            using var projection = new ManagedProjectionFixture(Kubernetes, cluster, NamespaceName);
            var metadata = new PostgresResourceDeploymentMetadata(
                projection.Projection.DeploymentId,
                projection.Projection.IsolationScopeId,
                projection.Projection.RuntimeCellId,
                projection.Projection.DeploymentTargetId,
                1);
            var journal = TestPhaseJournal.Current!;
            await projection.ApplyAsync("initial-secret");
            journal.Record(
                TestPhase.SnapshotAccepted,
                resourceId: projection.Projection.SecretId,
                revision: projection.Projection.DesiredVersion);

            journal.Record(TestPhase.Action, resourceId: cluster.Id, generation: metadata.Generation);
            await manager.ApplyAsync(
                Kubernetes,
                cluster,
                projection.Credentials,
                NamespaceName,
                Target.StorageClassName,
                Compute(),
                metadata,
                CancellationToken.None);
            await projection.ApplyAsync("rotated-secret");
            journal.Record(
                TestPhase.SnapshotAccepted,
                resourceId: projection.Projection.SecretId,
                revision: projection.Projection.DesiredVersion);
            journal.Record(TestPhase.Action, resourceId: cluster.Id, generation: metadata.Generation);
            await manager.ApplyAsync(
                Kubernetes,
                cluster,
                projection.Credentials,
                NamespaceName,
                Target.StorageClassName,
                Compute(),
                metadata,
                CancellationToken.None);

            var secret = await Kubernetes.CoreV1.ReadNamespacedSecretAsync(
                projection.Projection.KubernetesName,
                NamespaceName);

            Assert.Equal("postgres", Encoding.UTF8.GetString(secret.Data["username"]));
            Assert.Equal("rotated-secret", Encoding.UTF8.GetString(secret.Data["password"]));

            var native = JsonSerializer.SerializeToNode(
                await Kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
                    group: "postgresql.cnpg.io",
                    version: "v1",
                    namespaceParameter: NamespaceName,
                    plural: "clusters",
                    name: PostgresClusterKubernetesBuilder.buildResourceName(cluster.Id)))!;
            var labels = native["metadata"]!["labels"]!;
            Assert.Equal(metadata.RuntimeCellId, labels["pole.io/runtime-cell-id"]!.GetValue<string>());
            Assert.Equal(metadata.IsolationScopeId, labels["pole.io/isolation-scope-id"]!.GetValue<string>());
            Assert.Equal(metadata.DeploymentId, labels["pole.io/resource-deployment-id"]!.GetValue<string>());
        });
    }

    [KubernetesFact("cnpg")]
    public async Task OperatorDeleteAsync_LeavesProjectionForItsProviderAndRemovesCluster()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterDeploymentManager manager = new PostgresClusterDeploymentManager(CreateSettings());
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
        await manager.DeleteAsync(Kubernetes, cluster.Id, NamespaceName, CancellationToken.None);

        Assert.NotNull(
            await Kubernetes.CoreV1.ReadNamespacedSecretAsync(projection.Projection.KubernetesName, NamespaceName));
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

    private static PostgresCluster BuildOperatorCluster() => PostgresCluster.Create(
        PostgresTestData.TenantId,
        "postgres-k8s",
        ObjectId.GenerateNewId().ToString(),
        17,
        PostgresTestData.DefaultStorageProfileId,
        new CreatePostgresClusterOptions(FSharpOption<string>.Some(ObjectId.GenerateNewId().ToString()), 1, 5));

    private static PostgresResourceDeploymentMetadata Metadata() => new(
        "507f1f77bcf86cd799439044",
        ObjectId.GenerateNewId().ToString(),
        ObjectId.GenerateNewId().ToString(),
        PostgresTestData.DefaultDeploymentTargetId,
        1);

    private static KubernetesComputeConfiguration Compute() => new(
        new KubernetesComputeResourceValues(250, 256),
        new KubernetesComputeResourceValues(1000, 1024),
        ComputeArchitecture.Any,
        [],
        [],
        [],
        [],
        [],
        null);

    private static PostgresSettings CreateSettings() => new()
    {
        OperatorImage = new PostgresImageSettings { Server = "ghcr.io", Name = "cloudnative-pg/postgresql" },
    };

    private async Task WaitForClusterReadyAsync(
        string clusterId,
        int expectedReadyInstances,
        int timeoutSeconds = 180)
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
                return;

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw await CreateTimeoutExceptionAsync(
            $"Postgres cluster '{clusterId}' did not become ready in namespace '{NamespaceName}'. " +
            $"Last status: {lastStatus ?? "<missing>"}");
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
                    $"Host=127.0.0.1;Port={portForward.LocalPort};Database=postgres;Username=postgres;Password=initial-secret;SSL Mode=Disable;Timeout=5;Command Timeout=15");
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                return await command.ExecuteScalarAsync();
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException
                                                  or InvalidOperationException)
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
