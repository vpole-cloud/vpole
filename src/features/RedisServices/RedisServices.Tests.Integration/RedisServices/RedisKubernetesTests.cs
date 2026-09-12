using System.Collections.Immutable;
using System.Net;
using k8s;
using k8s.Autorest;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Tests.Integration.Infrastructure;
using Pole.Testing;
using StackExchange.Redis;
using Xunit;
using RedisEntity = Pole.Features.RedisServices.Domain.Redis;

namespace Pole.Features.RedisServices.Tests.Integration.RedisServices;

[Trait("Layer", "Kubernetes")]
[Trait("Concern", "Feature")]
[Trait("Scope", "SingleCluster")]
[Trait("Feature", "RedisServices")]
public sealed class RedisKubernetesTests(KubernetesClusterFixture fixture)
    : KubernetesIntegrationTestBase(fixture)
{
    [KubernetesFact]
    public async Task CreateAsync_WaitsForPodReadiness_AndRespondsToPing()
    {
        var (redis, storage, compute) = BuildTestRedis("redis-k8s");
        var redisInKubernetes = redis.ToKubernetes(
            NamespaceName,
            storage,
            compute);

        await RedisKubernetesResourceManager.CreateAsync(Kubernetes, redisInKubernetes);

        var statefulSet = await Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(redis.Name, NamespaceName);
        var service = await Kubernetes.CoreV1.ReadNamespacedServiceAsync(
            RedisInKubernetes.BuildServiceName(redis.Name),
            NamespaceName);

        await WaitForStatefulSetReadyAsync(redis.Name);

        await using var portForward = await KubernetesPortForwardSession.StartAsync(
            Target,
            NamespaceName,
            $"service/{RedisInKubernetes.BuildServiceName(redis.Name)}",
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
        var (redis, storage, compute) = BuildTestRedis("redis-k8s-replace");
        await RedisKubernetesResourceManager.CreateAsync(
            Kubernetes,
            redis.ToKubernetes(NamespaceName, storage, compute));

        var updatedCompute = compute with
        {
            ComputeProfileRevision = compute.ComputeProfileRevision + 1,
            Requests = new ResourceDeploymentComputeResourceValues(1000, 256),
            Limits = new ResourceDeploymentComputeResourceValues(2000, 512)
        };

        await RedisKubernetesResourceManager.ReplaceAsync(
            Kubernetes,
            redis.ToKubernetes(NamespaceName, storage, updatedCompute));

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
        var (redis, storage, compute) = BuildTestRedis("redis-k8s-delete");

        await RedisKubernetesResourceManager.CreateAsync(
            Kubernetes,
            redis.ToKubernetes(NamespaceName, storage, compute));
        await RedisKubernetesResourceManager.DeleteAsync(Kubernetes, redis.Name, NamespaceName);

        await AssertResourceDeletedAsync(() => Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(redis.Name, NamespaceName));
        await AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedServiceAsync(
            RedisInKubernetes.BuildServiceName(redis.Name),
            NamespaceName));
    }

    private (
        RedisEntity Redis,
        ResourceDeploymentStorageConfiguration Storage,
        ResourceDeploymentComputeConfiguration Compute)
        BuildTestRedis(string name)
    {
        var spec = new RedisSpec(
            new Storage(1, RedisTestData.DefaultStorageProfileId));

        var redis = RedisFactory.Create(
            tenantId: RedisTestData.TenantId,
            name: name,
            resourceGroupId: "rg-k8s",
            spec: spec,
            options: RedisFactory.DefaultCreateOptions with
            {
                Id = name,
                Tags = ImmutableHashSet.Create("cache")
            });
        var storage = new ResourceDeploymentStorageConfiguration(
            RedisTestData.DefaultStorageProfileId,
            StorageProfileRevision: 1,
            Target.StorageClassName);
        var compute = new ResourceDeploymentComputeConfiguration(
            "507f1f77bcf86cd799439019",
            ComputeProfileRevision: 1,
            DeploymentTargetId: "507f1f77bcf86cd799439020",
            DisplayName: "General purpose",
            new ResourceDeploymentComputeResourceValues(250, 128),
            new ResourceDeploymentComputeResourceValues(1000, 256),
            ComputeArchitecture.Any,
            [],
            [],
            [],
            [],
            [],
            RuntimeClassName: null);
        return (redis, storage, compute);
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

        throw await CreateTimeoutExceptionAsync(
            $"StatefulSet '{statefulSetName}' did not become ready in namespace '{NamespaceName}'.");
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

}
