using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceDeclarationCompositionTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public void Feature_registration_preserves_capability_discovery_and_scoped_identity()
    {
        var provider = Services.GetServices<IDeclarativeResourceProvider>().Single(item => item.Kind == "WebService");
        Assert.IsAssignableFrom<ITransactionalDeclarativeResourceProvider>(provider);
        Assert.IsAssignableFrom<IDeclarativeReferencePreparation>(provider);
        Assert.IsAssignableFrom<IDeclarativeResourceAdoptionProvider>(provider);
        Assert.Equal(WebServiceResourceDeployment.ResourceType,
            Assert.IsAssignableFrom<IDeclarativeRegisteredResourceProvider>(provider).ResourceType);
        Assert.Same(provider, Services.GetServices<IDeclarativeResourceLifecycleProvider>()
            .Single(item => item.Kind == "WebService"));
        Assert.Same(provider, Services.GetServices<IDeclarativeLifecycleDependencyProvider>()
            .Single(item => item.DependencyOwner == "WebServices"));
        Assert.Same(provider, Services.GetServices<IDeclarativePreviewReferenceProvider>()
            .Single(item => item is IDeclarativeResourceProvider { Kind: "WebService" }));
        Assert.Same(provider, Services.GetServices<IDeclarativeAdoptionPreviewProvider>()
            .Single(item => item is IDeclarativeResourceProvider { Kind: "WebService" }));
        Assert.Same(provider, Services.GetServices<IDeclarativeResourceProvider>()
            .Single(item => item.Kind == "WebService"));

        using var otherScope = Services.CreateScope();
        Assert.NotSame(provider, otherScope.ServiceProvider.GetServices<IDeclarativeResourceProvider>()
            .Single(item => item.Kind == "WebService"));
    }
}
