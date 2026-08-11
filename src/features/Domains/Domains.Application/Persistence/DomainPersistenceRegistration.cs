using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Database;
using Pole.Features.Domains.Contracts;

namespace Pole.Features.Domains.Application.Persistence;

public static class DomainPersistenceRegistration
{
    /// <summary>Registers only the provider-owned domain capabilities and transaction delegates.</summary>
    public static IServiceCollection AddDomainPersistence(this IServiceCollection services)
    {
        services.AddScoped<MongoDomainCapabilities>();
        services.AddScoped<ITenantDomainZoneCapability>(provider =>
            provider.GetRequiredService<MongoDomainCapabilities>());
        services.AddScoped<IHostnameBindingCapability>(provider =>
            provider.GetRequiredService<MongoDomainCapabilities>());
        services.TryAddSingleton<RunMongoTransaction<TenantDomainZoneSnapshot>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>()
                .InvokeAsync<TenantDomainZoneSnapshot>);
        services.TryAddSingleton<RunMongoTransaction<Pole.Features.Domains.Application.Endpoints.DeleteDomainZoneResponse>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>()
                .InvokeAsync<Pole.Features.Domains.Application.Endpoints.DeleteDomainZoneResponse>);
        return services;
    }
}
