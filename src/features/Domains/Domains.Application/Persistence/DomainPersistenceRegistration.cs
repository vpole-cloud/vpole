using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Database;
using Pole.Features.Domains.Application.Endpoints;
using Pole.Features.Domains.Application.Persistence.DomainZones;
using Pole.Features.Domains.Application.Persistence.HostnameBindings;
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
        services.AddScoped<MongoGetOrCreateGeneratedDomainZones>();
        services.AddScoped<GetOrCreateGeneratedDomainZonesOutcome>(provider =>
            provider.GetRequiredService<MongoGetOrCreateGeneratedDomainZones>().InvokeAsync);
        services.AddScoped<GetOrCreateGeneratedDomainZoneOutcome>(provider =>
            async (request, session, cancellationToken) =>
            {
                var result = await provider
                    .GetRequiredService<MongoGetOrCreateGeneratedDomainZones>()
                    .InvokeAsync([request], session, cancellationToken);
                return result.Map(zones => zones[0]);
            });

        services.AddScoped<MongoLoadHostnameBindingsForDeployment>();
        services.AddScoped<LoadHostnameBindingsForDeployment>(provider =>
            provider.GetRequiredService<MongoLoadHostnameBindingsForDeployment>().InvokeAsync);
        services.AddScoped<MongoLoadHostnameBindingsForDeploymentInTransaction>();
        services.AddScoped<LoadHostnameBindingsForDeploymentInTransaction>(provider =>
            provider.GetRequiredService<MongoLoadHostnameBindingsForDeploymentInTransaction>()
                .InvokeAsync);

        services.AddScoped<MongoHostnameBindingCleanup>();
        services.AddScoped<MongoCreateGeneratedHostnameBindings>();
        services.AddScoped<CreateGeneratedHostnameBindingsOutcome>(provider =>
            provider.GetRequiredService<MongoCreateGeneratedHostnameBindings>().InvokeAsync);
        services.AddScoped<CreateGeneratedHostnameBindingOutcome>(provider =>
            async (request, session, cancellationToken) =>
            {
                var result = await provider
                    .GetRequiredService<MongoCreateGeneratedHostnameBindings>()
                    .InvokeAsync([request], session, cancellationToken);
                return result.Map(bindings => bindings[0]);
            });

        services.AddScoped<MongoCreateCustomHostnameBinding>();
        services.AddScoped<CreateCustomHostnameBindingOutcome>(provider =>
            provider.GetRequiredService<MongoCreateCustomHostnameBinding>().InvokeAsync);
        services.AddScoped<MongoDeleteCustomHostnameBinding>();
        services.AddScoped<DeleteCustomHostnameBindingOutcome>(provider =>
            provider.GetRequiredService<MongoDeleteCustomHostnameBinding>().InvokeAsync);

        services.AddScoped<MongoDeleteHostnameBindingsForDeployment>();
        services.AddScoped<DeleteHostnameBindingsForDeployment>(provider =>
            provider.GetRequiredService<MongoDeleteHostnameBindingsForDeployment>().InvokeAsync);
        services.AddScoped<MongoDeleteHostnameBindingsForResource>();
        services.AddScoped<DeleteHostnameBindingsForResource>(provider =>
            provider.GetRequiredService<MongoDeleteHostnameBindingsForResource>().InvokeAsync);
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
