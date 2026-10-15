using Microsoft.Extensions.DependencyInjection;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Kubernetes;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Xunit;

namespace Pole.Features.RedisServices.Tests;

public sealed class RedisRegistrationTests
{
    [Fact]
    public void Registration_KeepsExistingProviderAndAddsRedisAdapter()
    {
        var services = new ServiceCollection();
        var other = ResourceDeploymentAdapterDescriptor.Create<OtherAdapter>(
            ResourceTypeId.Create("pole/test-provider"),
            ResourcePlacementCapability.SingleTarget);
        services.AddSingleton(other);
        services.AddRedisServicesFeature();
        using var provider = services.BuildServiceProvider();
        var descriptors = provider.GetServices<ResourceDeploymentAdapterDescriptor>().ToArray();
        Assert.Contains(other, descriptors);
        var redis = Assert.Single(descriptors, item => item.ResourceType.Value == "pole/redis");
        Assert.Equal(typeof(RedisResourceDeploymentAdapter), redis.AdapterType);
        var observer = Assert.Single(provider.GetServices<ResourceDeploymentObserverDescriptor>());
        Assert.Equal(typeof(RedisResourceDeploymentObserver), observer.ObserverType);
        Assert.Equal(typeof(RedisResourceDeploymentObserver), observer.BoundObserverType);
        Assert.Equal(typeof(RedisResourceDeploymentChangeSource), observer.ChangeSourceType);
        Assert.Contains(services, service => service.ServiceType == typeof(RedisResourceDeploymentChangeSource));
    }

    [Fact]
    public void Metadata_ReportsPersistedLifecycleAndRevisionWithoutPlacement()
    {
        var redis = RedisFactory.Create("tenant",
            "cache",
            "group",
            new RedisSpec(new Storage(1, "disk")),
            RedisFactory.DefaultCreateOptions);
        Assert.Equal("Active", redis.ToDto().Lifecycle);
        var deleting = redis.BeginDeletion(DateTime.UtcNow).ToDto();
        Assert.Equal("Deleting", deleting.Lifecycle);
        Assert.Equal(2, deleting.Revision);
        Assert.Null(deleting.Placement);
    }

    private sealed class OtherAdapter : IResourceDeploymentAdapter
    {
        public Task<ResourceDeploymentAdapterResult> ApplyAsync(ResourceDeploymentReconciliationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceDeploymentAdapterResult>(ResourceDeploymentAdapterResult.Succeeded.Create());

        public Task<ResourceDeploymentAdapterResult> DeleteAsync(ResourceDeploymentReconciliationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceDeploymentAdapterResult>(ResourceDeploymentAdapterResult.Succeeded.Create());
    }
}
