using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Features.RedisServices.Tests.Integration.RedisServices;

public sealed class RedisDeclarationCompositionTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public void Feature_registration_preserves_capability_discovery_and_scoped_identity()
    {
        var provider = Services.GetServices<IDeclarativeResourceProvider>().Single(item => item.Kind == "RedisService");
        Assert.IsAssignableFrom<ITransactionalDeclarativeResourceProvider>(provider);
        Assert.IsAssignableFrom<IDeclarativeReferencePreparation>(provider);
        Assert.IsAssignableFrom<IDeclarativeAdoptionPreviewProvider>(provider);
        Assert.IsAssignableFrom<IDeclarativePreviewReferenceProvider>(provider);
        Assert.IsAssignableFrom<IDeclarativeLifecycleWarningProvider>(provider);
        Assert.Equal(RedisResourceDeployment.ResourceType,
            Assert.IsAssignableFrom<IDeclarativeRegisteredResourceProvider>(provider).ResourceType);
        Assert.Same(provider, Services.GetServices<IDeclarativeResourceLifecycleProvider>()
            .Single(item => item.Kind == "RedisService"));
        Assert.Same(provider, Services.GetServices<IDeclarativeLifecycleDependencyProvider>()
            .Single(item => item.DependencyOwner == "RedisServices"));
        Assert.Same(provider, Services.GetServices<IDeclarativeResourceProvider>()
            .Single(item => item.Kind == "RedisService"));

        using var otherScope = Services.CreateScope();
        Assert.NotSame(provider, otherScope.ServiceProvider.GetServices<IDeclarativeResourceProvider>()
            .Single(item => item.Kind == "RedisService"));
    }
}
