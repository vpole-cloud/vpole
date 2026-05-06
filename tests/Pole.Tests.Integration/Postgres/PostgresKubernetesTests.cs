using System.Diagnostics;
using k8s;
using k8s.Autorest;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Npgsql;
using Pole.Domain.Models.FSharp;
using Pole.Features.Postgres.Common;
using Pole.Features.Postgres.Kubernetes;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using PostgresClusterModel = Pole.Domain.Models.FSharp.PostgresCluster;

namespace Pole.Tests.Integration.Postgres;

public sealed class PostgresKubernetesTests(KubernetesClusterFixture fixture)
    : KubernetesIntegrationTestBase(fixture)
{
    [Fact]
    public async Task OperatorCreateAsync_CreatesSecretAndClusterCustomResource()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterSecretStore secretStore = new KubernetesPostgresClusterSecretStore(Kubernetes);
        IPostgresClusterDeploymentBackend backend = new OperatorPostgresClusterDeploymentBackend(Kubernetes, CreateSettings());

        await secretStore.CreateAsync(NamespaceName, cluster.Id, "postgres-user", "initial-secret", CancellationToken.None);
        await backend.CreateAsync(cluster, "postgres-user", CancellationToken.None);

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

    [Fact]
    public async Task OperatorCreateAsync_WaitsForClusterReadiness_AndExecutesSimpleSql()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterSecretStore secretStore = new KubernetesPostgresClusterSecretStore(Kubernetes);
        IPostgresClusterDeploymentBackend backend = new OperatorPostgresClusterDeploymentBackend(Kubernetes, CreateSettings());

        await secretStore.CreateAsync(NamespaceName, cluster.Id, "postgres-user", "initial-secret", CancellationToken.None);
        await backend.CreateAsync(cluster, "postgres-user", CancellationToken.None);
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

    [Fact]
    public async Task UpdatePasswordAsync_RewritesSecret()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterSecretStore secretStore = new KubernetesPostgresClusterSecretStore(Kubernetes);

        await secretStore.CreateAsync(NamespaceName, cluster.Id, "postgres-user", "initial-secret", CancellationToken.None);
        await secretStore.UpdatePasswordAsync(NamespaceName, cluster.Id, "rotated-secret", CancellationToken.None);

        var secret = await Kubernetes.CoreV1.ReadNamespacedSecretAsync(
            PostgresClusterKubernetesBuilder.buildSecretName(cluster.Id),
            NamespaceName);

        Assert.Equal("postgres-user", System.Text.Encoding.UTF8.GetString(secret.Data["username"]));
        Assert.Equal("rotated-secret", System.Text.Encoding.UTF8.GetString(secret.Data["password"]));
    }

    [Fact]
    public async Task OperatorDeleteAsync_RemovesSecretAndCluster()
    {
        var cluster = BuildOperatorCluster();
        IPostgresClusterSecretStore secretStore = new KubernetesPostgresClusterSecretStore(Kubernetes);
        IPostgresClusterDeploymentBackend backend = new OperatorPostgresClusterDeploymentBackend(Kubernetes, CreateSettings());

        await secretStore.CreateAsync(NamespaceName, cluster.Id, "postgres-user", "initial-secret", CancellationToken.None);
        await backend.CreateAsync(cluster, "postgres-user", CancellationToken.None);
        await backend.DeleteAsync(cluster.Id, NamespaceName, CancellationToken.None);

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

    [Fact]
    public async Task StatefulSetCreateAsync_WaitsForPodReadiness_AndExecutesSimpleSql()
    {
        var cluster = BuildStatefulSetCluster();
        IPostgresClusterSecretStore secretStore = new KubernetesPostgresClusterSecretStore(Kubernetes);
        IPostgresClusterDeploymentBackend backend = new StatefulSetPostgresClusterDeploymentBackend(Kubernetes, CreateSettings());

        await secretStore.CreateAsync(NamespaceName, cluster.Id, "postgres-user", "initial-secret", CancellationToken.None);
        await backend.CreateAsync(cluster, "postgres-user", CancellationToken.None);
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

    [Fact]
    public async Task StatefulSetDeleteAsync_RemovesResources()
    {
        var cluster = BuildStatefulSetCluster();
        IPostgresClusterSecretStore secretStore = new KubernetesPostgresClusterSecretStore(Kubernetes);
        IPostgresClusterDeploymentBackend backend = new StatefulSetPostgresClusterDeploymentBackend(Kubernetes, CreateSettings());

        await secretStore.CreateAsync(NamespaceName, cluster.Id, "postgres-user", "initial-secret", CancellationToken.None);
        await backend.CreateAsync(cluster, "postgres-user", CancellationToken.None);
        await backend.DeleteAsync(cluster.Id, NamespaceName, CancellationToken.None);

        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedSecretAsync(
            PostgresClusterKubernetesBuilder.buildSecretName(cluster.Id),
            NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(cluster.Id, NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedServiceAsync($"{cluster.Id}-headless", NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedServiceAsync($"{cluster.Id}-rw", NamespaceName));
    }

    private PostgresClusterModel BuildOperatorCluster() =>
        new("postgres-k8s", "postgres-k8s", "rg-k8s", new PostgresClusterSpec(1, 5, NamespaceName, 17, PostgresDeploymentMode.Operator));

    private PostgresClusterModel BuildStatefulSetCluster() =>
        new("postgres-sts", "postgres-sts", "rg-k8s", new PostgresClusterSpec(1, 5, NamespaceName, 17, PostgresDeploymentMode.StatefulSet));

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
