using System.Collections.Immutable;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Kubernetes;
using Pole.Features.Shared.Models;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using Redis = Pole.Features.RedisServices.Domain.Redis;

namespace Pole.Tests.Integration.RedisServices;

public sealed class RedisManifestTests
{
    [Fact]
    public void ToStatefulSet_UsesRedisImageStorageResourcesAndManagedMetadata()
    {
        var manifest = CreateSubject();

        var statefulSet = manifest.ToStatefulSet();
        var container = Assert.Single(statefulSet.Spec.Template.Spec.Containers);
        var volumeClaim = Assert.Single(statefulSet.Spec.VolumeClaimTemplates);

        Assert.Equal("apps/v1", statefulSet.ApiVersion);
        Assert.Equal("StatefulSet", statefulSet.Kind);
        Assert.Equal("redis-cache", statefulSet.Metadata.Name);
        Assert.Equal("pole-cache", statefulSet.Metadata.NamespaceProperty);
        Assert.Equal("redis-cache-svc", statefulSet.Spec.ServiceName);
        Assert.Equal(1, statefulSet.Spec.Replicas);
        Assert.Equal("redis:8-alpine", container.Image);
        Assert.Equal("redis", container.Name);
        Assert.Equal(6379, Assert.Single(container.Ports).ContainerPort);
        Assert.Equal("/data", Assert.Single(container.VolumeMounts).MountPath);
        Assert.Equal("redis-data", volumeClaim.Metadata.Name);
        Assert.Equal("fast-cache", volumeClaim.Spec.StorageClassName);
        Assert.Equal("12Gi", volumeClaim.Spec.Resources.Requests["storage"].ToString());
        Assert.Equal("1", container.Resources.Requests["cpu"].ToString());
        Assert.Equal("256Mi", container.Resources.Requests["memory"].ToString());
        Assert.Equal("2", container.Resources.Limits["cpu"].ToString());
        Assert.Equal("512Mi", container.Resources.Limits["memory"].ToString());
        Assert.Equal("pole-server", statefulSet.Spec.Template.Metadata.Annotations["app.kubernetes.io/managed-by"]);
        Assert.Equal("redis-1", statefulSet.Spec.Template.Metadata.Annotations["pole.server/redisId"]);
    }

    [Fact]
    public void ToService_UsesStableHeadlessServiceAndManagedMetadata()
    {
        var manifest = CreateSubject();

        var service = manifest.ToService();
        var port = Assert.Single(service.Spec.Ports);

        Assert.Equal("v1", service.ApiVersion);
        Assert.Equal("Service", service.Kind);
        Assert.Equal("redis-cache-svc", service.Metadata.Name);
        Assert.Equal("pole-cache", service.Metadata.NamespaceProperty);
        Assert.Equal("ClusterIP", service.Spec.Type);
        Assert.Equal("None", service.Spec.ClusterIP);
        Assert.Equal("redis-cache", service.Spec.Selector["app"]);
        Assert.Equal("redis", port.Name);
        Assert.Equal(6379, port.Port);
        Assert.Equal("6379", port.TargetPort.Value);
        Assert.Equal("TCP", port.Protocol);
        Assert.Equal("pole-server", service.Metadata.Annotations["app.kubernetes.io/managed-by"]);
        Assert.Equal("redis-1", service.Metadata.Annotations["pole.server/redisId"]);
    }

    private static RedisInKubernetes CreateSubject() =>
        new(BuildRedis(
            "redis-1",
            "redis-cache",
            new RedisSpec(
                "pole-cache",
                new Storage(12, "fast-cache"),
                new Resources(
                    Requests: new Resource(1, 256),
                    Limits: new Resource(2, 512)))));

    private static Redis BuildRedis(string id, string name, RedisSpec spec) =>
        RedisFactory.Create(
            tenantId: TestData.TenantId,
            name: name,
            resourceGroupId: "rg-1",
            spec: spec,
            options: RedisFactory.DefaultCreateOptions with
            {
                Id = id,
                Tags = ImmutableHashSet.Create("cache")
            });
}
