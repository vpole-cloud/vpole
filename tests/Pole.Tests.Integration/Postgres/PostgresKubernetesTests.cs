using System.Diagnostics;
using k8s;
using k8s.Autorest;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.FSharp.Core;
using MongoDB.Bson;
using Npgsql;
using Pole.Features.Core.Kubernetes;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Domain;
using Pole.Features.Postgres.Application.Kubernetes;
using Pole.Features.Shared.Models;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using PostgresCluster = Pole.Features.Postgres.Domain.PostgresCluster;

namespace Pole.Tests.Integration.Postgres;

public sealed class PostgresKubernetesTests(KubernetesClusterFixture fixture)
    : KubernetesIntegrationTestBase(fixture)
{
    [KubernetesFact]
    public async Task OperatorCreateAsync_CreatesSecretAndClusterCustomResource()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterDeploymentManager manager =
            new PostgresClusterDeploymentManager(CreateSettings());

        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            Credentials("initial-secret"),
            NamespaceName,
            "standard",
            Compute(),
            Metadata(),
            CancellationToken.None);

        var secret = await Kubernetes.CoreV1.ReadNamespacedSecretAsync(
            PostgresClusterKubernetesBuilder.buildSecretName(cluster.Id),
            NamespaceName);
        var clusterResource = await Kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
            group: "postgresql.cnpg.io",
            version: "v1",
            namespaceParameter: NamespaceName,
            plural: "clusters",
            name: cluster.Id);

        Assert.Equal("postgres-user", System.Text.Encoding.UTF8.GetString(secret.Data["username"]));
        Assert.Equal("initial-secret", System.Text.Encoding.UTF8.GetString(secret.Data["password"]));
        Assert.NotNull(clusterResource);
    }

    [KubernetesFact]
    public async Task OperatorCreateAsync_WaitsForClusterReadiness_AndExecutesSimpleSql()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterDeploymentManager manager =
            new PostgresClusterDeploymentManager(CreateSettings());

        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            Credentials("initial-secret"),
            NamespaceName,
            "standard",
            Compute(),
            Metadata(),
            CancellationToken.None);
        await WaitForClusterReadyAsync(cluster.Id, expectedReadyInstances: cluster.Spec.Instances);

        await using var portForward = await StartPortForwardAsync(
            NamespaceName,
            PostgresClusterKubernetesBuilder.buildReadWriteServiceName(cluster.Id),
            remotePort: 5432);

        await using var connection = new NpgsqlConnection(
            $"Host=127.0.0.1;Port={portForward.LocalPort};Database={PostgresClusterKubernetesBuilder.buildDatabaseName(cluster.Id)};Username=postgres-user;Password=initial-secret;SSL Mode=Disable;Timeout=15;Command Timeout=15");
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT 1", connection);
        var result = await command.ExecuteScalarAsync();

        Assert.Equal(1, result);
    }

    [KubernetesFact]
    public async Task UpdatePasswordAsync_RewritesSecret()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterDeploymentManager manager =
            new PostgresClusterDeploymentManager(CreateSettings());

        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            Credentials("initial-secret"),
            NamespaceName,
            "standard",
            Compute(),
            Metadata(),
            CancellationToken.None);
        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            Credentials("rotated-secret"),
            NamespaceName,
            "standard",
            Compute(),
            Metadata(),
            CancellationToken.None);

        var secret = await Kubernetes.CoreV1.ReadNamespacedSecretAsync(
            PostgresClusterKubernetesBuilder.buildSecretName(cluster.Id),
            NamespaceName);

        Assert.Equal("postgres-user", System.Text.Encoding.UTF8.GetString(secret.Data["username"]));
        Assert.Equal("rotated-secret", System.Text.Encoding.UTF8.GetString(secret.Data["password"]));
    }

    [KubernetesFact]
    public async Task OperatorDeleteAsync_RemovesSecretAndCluster()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterDeploymentManager manager =
            new PostgresClusterDeploymentManager(CreateSettings());

        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            Credentials("initial-secret"),
            NamespaceName,
            "standard",
            Compute(),
            Metadata(),
            CancellationToken.None);
        await manager.DeleteAsync(
            Kubernetes,
            cluster.Id,
            NamespaceName,
            cluster.Spec.DeploymentMode,
            CancellationToken.None);

        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedSecretAsync(
            PostgresClusterKubernetesBuilder.buildSecretName(cluster.Id),
            NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.CustomObjects.GetNamespacedCustomObjectAsync(
            group: "postgresql.cnpg.io",
            version: "v1",
            namespaceParameter: NamespaceName,
            plural: "clusters",
            name: cluster.Id));
    }

    [KubernetesFact]
    public async Task StatefulSetCreateAsync_WaitsForPodReadiness_AndExecutesSimpleSql()
    {
        var cluster = BuildStatefulSetCluster();
        IPostgresClusterDeploymentManager manager =
            new PostgresClusterDeploymentManager(CreateSettings());

        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            Credentials("initial-secret"),
            NamespaceName,
            "standard",
            Compute(),
            Metadata(),
            CancellationToken.None);
        await WaitForStatefulSetReadyAsync(cluster.Id);

        await using var portForward = await StartPortForwardAsync(
            NamespaceName,
            PostgresClusterKubernetesBuilder.buildReadWriteServiceName(cluster.Id),
            remotePort: 5432);

        await using var connection = new NpgsqlConnection(
            $"Host=127.0.0.1;Port={portForward.LocalPort};Database={PostgresClusterKubernetesBuilder.buildDatabaseName(cluster.Id)};Username=postgres-user;Password=initial-secret;SSL Mode=Disable;Timeout=15;Command Timeout=15");
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("SELECT 1", connection);
        var result = await command.ExecuteScalarAsync();

        Assert.Equal(1, result);
    }

    [KubernetesFact]
    public async Task StatefulSetDeleteAsync_RemovesResources()
    {
        var cluster = BuildStatefulSetCluster();
        IPostgresClusterDeploymentManager manager =
            new PostgresClusterDeploymentManager(CreateSettings());

        await manager.ApplyAsync(
            Kubernetes,
            cluster,
            Credentials("initial-secret"),
            NamespaceName,
            "standard",
            Compute(),
            Metadata(),
            CancellationToken.None);
        await manager.DeleteAsync(
            Kubernetes,
            cluster.Id,
            NamespaceName,
            cluster.Spec.DeploymentMode,
            CancellationToken.None);

        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedSecretAsync(
            PostgresClusterKubernetesBuilder.buildSecretName(cluster.Id),
            NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(cluster.Id, NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedServiceAsync($"{cluster.Id}-headless", NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedServiceAsync($"{cluster.Id}-rw", NamespaceName));
    }

    private PostgresCluster BuildOperatorCluster() =>
        BuildCluster(PostgresDeploymentMode.Operator);

    private PostgresCluster BuildStatefulSetCluster() =>
        BuildCluster(PostgresDeploymentMode.StatefulSet);

    private static PostgresCluster BuildCluster(PostgresDeploymentMode mode) =>
        PostgresCluster.Create(
            TestData.TenantId,
            mode == PostgresDeploymentMode.Operator
                ? "postgres-k8s"
                : "postgres-sts",
            ObjectId.GenerateNewId().ToString(),
            17,
            mode,
            TestData.DefaultStorageProfileId,
            new CreatePostgresClusterOptions(
                FSharpOption<string>.Some(ObjectId.GenerateNewId().ToString()),
                1,
                5));

    private static PostgresClusterCredentials Credentials(string password) =>
        new("postgres-user", password);

    private static PostgresResourceDeploymentMetadata Metadata() =>
        new(
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            TestData.DefaultDeploymentTargetId,
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
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string? lastStatus = null;

        while (DateTime.UtcNow < deadline)
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

        throw new TimeoutException(
            $"Postgres cluster '{clusterId}' did not become ready in namespace '{NamespaceName}'. Last status: {lastStatus ?? "<missing>"}");
    }

    private async Task WaitForStatefulSetReadyAsync(string statefulSetName, int timeoutSeconds = 180)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            var statefulSet = await Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(statefulSetName, NamespaceName);
            var readyReplicas = statefulSet.Status?.ReadyReplicas ?? 0;
            if (readyReplicas >= 1)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException($"StatefulSet '{statefulSetName}' did not become ready in namespace '{NamespaceName}'.");
    }

    private static async Task<PortForwardSession> StartPortForwardAsync(string namespaceName, string serviceName, int remotePort)
    {
        var localPort = GetFreeTcpPort();
        var startInfo = new ProcessStartInfo
        {
            FileName = "kubectl",
            ArgumentList =
            {
                "-n", namespaceName,
                "port-forward",
                $"svc/{serviceName}",
                $"{localPort}:{remotePort}"
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("Failed to start kubectl port-forward process.");

        try
        {
            await WaitForLocalPortAsync(localPort, timeoutSeconds: 20);
            return new PortForwardSession(process, localPort);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            var errorOutput = await process.StandardError.ReadToEndAsync();
            throw new TimeoutException(
                $"kubectl port-forward for service '{serviceName}' did not become ready. stderr: {errorOutput}");
        }
    }

    private static async Task WaitForLocalPortAsync(int port, int timeoutSeconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        throw new TimeoutException($"Local port {port} did not become available.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task AssertResourceDeletedAsync<T>(Func<Task<T>> readCall, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
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

    private sealed class PortForwardSession(Process process, int localPort) : IAsyncDisposable
    {
        public int LocalPort { get; } = localPort;

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
