using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Database;
using Pole.Features.Domains.Application.Endpoints;
using Pole.Features.Domains.Contracts;
using Pole.ResultType;

namespace Pole.Features.Domains.Application.Persistence;

public static class DomainPersistenceRegistration
{
    /// <summary>Registers only the provider-owned domain capabilities and transaction delegates.</summary>
    public static IServiceCollection AddDomainPersistence(this IServiceCollection services)
    {
        services.AddScoped<MongoDomainPersistence>();
        services.AddScoped<LoadTenantDomainZone>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>().GetAsync);
        services.AddScoped<LoadTenantDomainZones>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>().ListAsync);
        services.AddScoped<CreateManagedDomainZoneReservation>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .CreateManagedReservationOutcomeAsync);
        services.AddScoped<DeleteManagedDomainZone>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .DeleteManagedReservationAsync);
        services.AddScoped<GetOrCreateGeneratedDomainZone>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .GetOrCreateGeneratedAsync);
        services.AddScoped<GetOrCreateGeneratedDomainZones>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .GetOrCreateGeneratedSetAsync);
        services.AddScoped<LoadHostnameBindingsForDeployment>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .ListForDeploymentAsync);
        services.AddScoped<LoadHostnameBindingsForDeploymentInTransaction>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .ListForDeploymentInTransactionAsync);
        services.AddScoped<CreateGeneratedHostnameBinding>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .CreateGeneratedAsync);
        services.AddScoped<CreateGeneratedHostnameBindings>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .CreateGeneratedSetAsync);
        services.AddScoped<CreateCustomHostnameBinding>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .CreateCustomAsync);
        services.AddScoped<DeleteCustomHostnameBinding>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .DeleteCustomAsync);
        services.AddScoped<DeleteHostnameBindingsForDeployment>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .DeleteAllForDeploymentAsync);
        services.AddScoped<DeleteHostnameBindingsForResource>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .DeleteAllForResourceAsync);
        services.TryAddSingleton<RunMongoOutcomeTransaction<
            Result<TenantDomainZoneSnapshot, CreateDomainZoneError>>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<
                    Result<TenantDomainZoneSnapshot, CreateDomainZoneError>>);
        services.TryAddSingleton<RunMongoTransaction<Pole.Features.Domains.Application.Endpoints.DeleteDomainZoneResponse>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>()
                .InvokeAsync<Pole.Features.Domains.Application.Endpoints.DeleteDomainZoneResponse>);
        return services;
    }
}
