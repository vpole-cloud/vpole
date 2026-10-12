using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.Realtime.Contracts;
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
        services.AddHttpProblemMappings(DomainContractHttpMappings.All);
        services.AddHttpProblemMappings(DomainHttpMappings.All);
        return services;
    }
}
