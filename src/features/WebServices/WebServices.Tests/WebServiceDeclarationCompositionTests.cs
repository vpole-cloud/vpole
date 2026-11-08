using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Features.GitOps.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Declarations;
using Xunit;

namespace Pole.Features.WebServices.Tests;

public sealed class WebServiceDeclarationCompositionTests
{
    [Fact]
    public void Feature_registration_preserves_capability_discovery_and_scoped_identity()
    {
        var services = new ServiceCollection();
        services.AddWebServicesFeature();
        var descriptor = Assert.Single(services.Where(item => item.ServiceType == typeof(DeclarativeWebServiceProvider)));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(typeof(DeclarativeWebServiceProvider), descriptor.ImplementationType);
        // Provider operations are unused. Preserve the production aliases while supplying its collaborators.
        services.Replace(ServiceDescriptor.Scoped<DeclarativeWebServiceProvider>(
            _ => new(null!, null!, null!, null!, null!, null!, null!)));
        using var root = services.BuildServiceProvider(validateScopes: true);
        using var scope = root.CreateScope();
        var Services = scope.ServiceProvider;
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
