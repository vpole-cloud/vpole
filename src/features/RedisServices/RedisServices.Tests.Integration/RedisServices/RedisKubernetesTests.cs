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
    public async Task Lifecycle_creates_a_ready_instance_updates_compute_and_deletes_resources()
    {
        var (redis, storage, compute) = BuildTestRedis("redis-k8s");
        var redisInKubernetes = redis.ToKubernetes(
            NamespaceName,
            storage,
            compute,
            new Pole.Features.Core.Kubernetes.WorkloadIdentityLabels(redis.TenantId, redis.ResourceGroupId, "scope-44", "cell-44", "target-44", "Redis", redis.Id, "deployment-44"));

        await RedisKubernetesResourceManager.CreateAsync(Kubernetes, redisInKubernetes);

        var statefulSet = await Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(redis.Name, NamespaceName);
        var service = await Kubernetes.CoreV1.ReadNamespacedServiceAsync(
            RedisInKubernetes.BuildServiceName(redis.Name),
            NamespaceName);

        await WaitForStatefulSetReadyAsync(redis.Name);

        TimeSpan pong;
        await using (var portForward = await KubernetesPortForwardSession.StartAsync(
                         Target,
                         NamespaceName,
                         $"service/{RedisInKubernetes.BuildServiceName(redis.Name)}",
                         remotePort: 6379))
        await using (var connection = await ConnectionMultiplexer.ConnectAsync(
                         $"127.0.0.1:{portForward.LocalPort},connectTimeout=15000,syncTimeout=15000,abortConnect=false"))
        {
            pong = await connection.GetDatabase().PingAsync();
        }

        Assert.Equal("redis:8-alpine", statefulSet.Spec.Template.Spec.Containers.Single().Image);
        Assert.Equal("None", service.Spec.ClusterIP);
        Assert.Equal("redis", service.Spec.Ports.Single().Name);
        Assert.True(pong > TimeSpan.Zero);

        var updatedCompute = compute with
        {
            ComputeProfileRevision = compute.ComputeProfileRevision + 1,
            Requests = new ResourceDeploymentComputeResourceValues(1000, 256),
            Limits = new ResourceDeploymentComputeResourceValues(2000, 512)
        };

        await RedisKubernetesResourceManager.ReplaceAsync(
            Kubernetes,
            redis.ToKubernetes(NamespaceName, storage, updatedCompute,
            new Pole.Features.Core.Kubernetes.WorkloadIdentityLabels(redis.TenantId, redis.ResourceGroupId, "scope-44", "cell-44", "target-44", "Redis", redis.Id, "deployment-44")));

        var updatedStatefulSet = await Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(redis.Name, NamespaceName);
        var updatedService = await Kubernetes.CoreV1.ReadNamespacedServiceAsync(
            RedisInKubernetes.BuildServiceName(redis.Name),
            NamespaceName);
        var container = updatedStatefulSet.Spec.Template.Spec.Containers.Single();

        Assert.Equal("1", container.Resources.Requests["cpu"].ToString());
        Assert.Equal("256Mi", container.Resources.Requests["memory"].ToString());
        Assert.Equal("2", container.Resources.Limits["cpu"].ToString());
        Assert.Equal("512Mi", container.Resources.Limits["memory"].ToString());
        Assert.Equal(NamespaceName, updatedService.Metadata.NamespaceProperty);

        await RedisKubernetesResourceManager.DeleteAsync(Kubernetes, redis.Name, NamespaceName);

        await Task.WhenAll(
            AssertResourceDeletedAsync(() => Kubernetes.AppsV1.ReadNamespacedStatefulSetAsync(redis.Name, NamespaceName)),
            AssertResourceDeletedAsync(() => Kubernetes.CoreV1.ReadNamespacedServiceAsync(
                RedisInKubernetes.BuildServiceName(redis.Name),
                NamespaceName)));
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
