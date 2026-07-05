using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using k8s;
using k8s.Autorest;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.Shared.Models;
using Pole.Tests.Integration.Infrastructure;
using StackExchange.Redis;
using Xunit;
using RedisEntity = Pole.Features.RedisServices.Domain.Redis;

namespace Pole.Tests.Integration.RedisServices;

public sealed class RedisKubernetesTests(KubernetesClusterFixture fixture)
    : KubernetesIntegrationTestBase(fixture)
{
    [KubernetesFact]
    public async Task CreateAsync_WaitsForPodReadiness_AndRespondsToPing()
    {
        var redis = await BuildTestRedisAsync("redis-k8s");
        var redisInKubernetes = redis.ToKubernetes();

        await RedisKubernetesResourceManager.CreateAsync(Kubernetes, redisInKubernetes);

        var statefulSet = await Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(redis.Name, NamespaceName);
        var service = await Kubernetes.CoreV1.ReadNamespacedServiceAsync(
            RedisInKubernetes.BuildServiceName(redis.Name),
            NamespaceName);

        await WaitForStatefulSetReadyAsync(redis.Name);

        await using var portForward = await StartPortForwardAsync(
            NamespaceName,
            RedisInKubernetes.BuildServiceName(redis.Name),
            remotePort: 6379);

        await using var connection = await ConnectionMultiplexer.ConnectAsync(
            $"127.0.0.1:{portForward.LocalPort},connectTimeout=15000,syncTimeout=15000,abortConnect=false");
        var pong = await connection.GetDatabase().PingAsync();

        Assert.Equal("redis:8-alpine", statefulSet.Spec.Template.Spec.Containers.Single().Image);
        Assert.Equal("None", service.Spec.ClusterIP);
        Assert.Equal("redis", service.Spec.Ports.Single().Name);
        Assert.True(pong > TimeSpan.Zero);
    }

    [KubernetesFact]
    public async Task ReplaceAsync_UpdatesStatefulSetAndService()
    {
        var redis = await BuildTestRedisAsync("redis-k8s-replace");
        await RedisKubernetesResourceManager.CreateAsync(Kubernetes, redis.ToKubernetes());

        var updated = redis with
        {
            Spec = redis.Spec with
            {
                Resources = new Resources(
                    Requests: new Resource(1, 256),
                    Limits: new Resource(2, 512))
            }
        };

        await RedisKubernetesResourceManager.ReplaceAsync(Kubernetes, updated.ToKubernetes());

        var statefulSet = await Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(redis.Name, NamespaceName);
        var service = await Kubernetes.CoreV1.ReadNamespacedServiceAsync(
            RedisInKubernetes.BuildServiceName(redis.Name),
            NamespaceName);
        var container = statefulSet.Spec.Template.Spec.Containers.Single();

        Assert.Equal("1", container.Resources.Requests["cpu"].ToString());
        Assert.Equal("256Mi", container.Resources.Requests["memory"].ToString());
        Assert.Equal("2", container.Resources.Limits["cpu"].ToString());
        Assert.Equal("512Mi", container.Resources.Limits["memory"].ToString());
        Assert.Equal(NamespaceName, service.Metadata.NamespaceProperty);
    }

    [KubernetesFact]
    public async Task DeleteAsync_RemovesResources()
    {
        var redis = await BuildTestRedisAsync("redis-k8s-delete");

        await RedisKubernetesResourceManager.CreateAsync(Kubernetes, redis.ToKubernetes());
        await RedisKubernetesResourceManager.DeleteAsync(Kubernetes, redis.Name, NamespaceName);

        await AssertResourceDeletedAsync(() => Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(redis.Name, NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedServiceAsync(
            RedisInKubernetes.BuildServiceName(redis.Name),
            NamespaceName));
    }

    private async Task<RedisEntity> BuildTestRedisAsync(string name)
    {
        var storageClassName = await ResolveStorageClassNameAsync();
        var spec = new RedisSpec(
            NamespaceName,
            new Storage(1, storageClassName),
            new Resources(
                Requests: new Resource(1, 128),
                Limits: new Resource(2, 256)));

        return RedisFactory.Create(
            tenantId: TestData.TenantId,
            name: name,
            resourceGroupId: "rg-k8s",
            spec: spec,
            options: RedisFactory.DefaultCreateOptions with
            {
                Id = name,
                Tags = ImmutableHashSet.Create("cache")
            });
    }

    private async Task<string> ResolveStorageClassNameAsync()
    {
        var configured = Environment.GetEnvironmentVariable("POLE_K8S_REDIS_STORAGE_CLASS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var storageClasses = await Kubernetes.StorageV1.ListStorageClassAsync();
        var defaultStorageClass = storageClasses.Items.FirstOrDefault(storageClass =>
            storageClass.Metadata.Annotations is not null &&
            (IsDefaultStorageClass(storageClass.Metadata.Annotations, "storageclass.kubernetes.io/is-default-class") ||
             IsDefaultStorageClass(storageClass.Metadata.Annotations, "storageclass.beta.kubernetes.io/is-default-class")));

        return defaultStorageClass?.Metadata.Name
               ?? throw new InvalidOperationException(
                   "No default Kubernetes StorageClass was found. Set POLE_K8S_REDIS_STORAGE_CLASS before running RedisKubernetesTests.");
    }

    private static bool IsDefaultStorageClass(IDictionary<string, string> annotations, string key) =>
        annotations.TryGetValue(key, out var value) &&
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

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

