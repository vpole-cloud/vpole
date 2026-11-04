using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Realtime.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Domains.Application.Persistence.DomainZones;
using Pole.Features.Domains.Application.Persistence.HostnameBindings;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.Persistence;
using Pole.Features.Domains.Contracts.HttpMapping;

namespace Pole.Features.Domains.Application;

public static class DomainsFeatureRegistration
{
    public static IServiceCollection AddDomainsFeature(
        this IServiceCollection services)
    {
        services.AddSingleton(new RealtimeTopic("domains", RealtimeScope.Tenant));
        services.AddDomainPersistence();
        services.AddScoped<DeclarativeDomainZoneProvider>();
        services.AddScoped<DeclarativeHostnameBindingProvider>();
        services.AddScoped<IDeclarativeResourceProvider>(provider =>
            provider.GetRequiredService<DeclarativeDomainZoneProvider>());
        services.AddScoped<IDeclarativeResourceProvider>(provider =>
            provider.GetRequiredService<DeclarativeHostnameBindingProvider>());
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(provider =>
            provider.GetRequiredService<DeclarativeDomainZoneProvider>());
        services.AddScoped<IDeclarativeResourceLifecycleProvider>(provider =>
            provider.GetRequiredService<DeclarativeHostnameBindingProvider>());
        services.AddScoped<IDeclarativeLifecycleDependencyProvider, DomainLifecycleDependencies>();
        services.AddHttpProblemMappings(DomainContractHttpMappings.All);
        services.AddHttpProblemMappings(DomainHttpMappings.All);
        return services;
    }
}
