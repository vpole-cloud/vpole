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
        services.AddScoped<MongoLoadTenantDomainZone>();
        services.AddScoped<LoadTenantDomainZone>(provider =>
            provider.GetRequiredService<MongoLoadTenantDomainZone>().InvokeAsync);
        services.AddScoped<MongoLoadTenantDomainZones>();
        services.AddScoped<LoadTenantDomainZones>(provider =>
            provider.GetRequiredService<MongoLoadTenantDomainZones>().InvokeAsync);
        services.AddScoped<MongoCreateManagedDomainZoneReservation>();
        services.AddScoped<CreateManagedDomainZoneReservation>(provider =>
            provider.GetRequiredService<MongoCreateManagedDomainZoneReservation>()
                .InvokeAsync);
        services.AddScoped<MongoDeleteManagedDomainZone>();
        services.AddScoped<DeleteManagedDomainZone>(provider =>
            provider.GetRequiredService<MongoDeleteManagedDomainZone>().InvokeAsync);
        services.AddScoped<MongoDomainPersistence>();
        services.AddScoped<GetOrCreateGeneratedDomainZoneOutcome>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .GetOrCreateGeneratedOutcomeAsync);
        services.AddScoped<GetOrCreateGeneratedDomainZonesOutcome>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .GetOrCreateGeneratedSetOutcomeAsync);
        services.AddScoped<LoadHostnameBindingsForDeployment>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .ListForDeploymentAsync);
        services.AddScoped<LoadHostnameBindingsForDeploymentInTransaction>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .ListForDeploymentInTransactionAsync);
        services.AddScoped<CreateGeneratedHostnameBindingOutcome>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .CreateGeneratedOutcomeAsync);
        services.AddScoped<CreateGeneratedHostnameBindingsOutcome>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .CreateGeneratedSetOutcomeAsync);
        services.AddScoped<CreateCustomHostnameBindingOutcome>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .CreateCustomOutcomeAsync);
        services.AddScoped<DeleteCustomHostnameBindingOutcome>(provider =>
            provider.GetRequiredService<MongoDomainPersistence>()
                .DeleteCustomOutcomeAsync);
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
        services.TryAddSingleton<RunMongoOutcomeTransaction<
            DeleteDomainZoneOutcome>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<DeleteDomainZoneOutcome>);
        return services;
    }
}
