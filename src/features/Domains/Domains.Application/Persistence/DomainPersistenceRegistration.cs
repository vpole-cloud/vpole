using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.Database;
using Pole.Features.Domains.Application.Endpoints;
using Pole.Features.Domains.Application.Persistence.DomainZones;
using Pole.Features.Domains.Application.Persistence.HostnameBindings;
using Pole.Features.Domains.Contracts;

namespace Pole.Features.Domains.Application.Persistence;

public static class DomainPersistenceRegistration
{
    /// <summary>Registers only the provider-owned domain capabilities and transaction delegates.</summary>
    public static IServiceCollection AddDomainPersistence(this IServiceCollection services)
    {
        services.AddScoped<MongoDomainPlanning>();
        services.AddScoped<ResolveNamedDomainZone>(provider =>
            provider.GetRequiredService<MongoDomainPlanning>().ResolveZoneAsync);
        services.AddScoped<ResolveNamedHostnameBinding>(provider =>
            provider.GetRequiredService<MongoDomainPlanning>().ResolveBindingAsync);
        services.AddScoped<LoadDomainZoneForPlanning>(provider =>
            provider.GetRequiredService<MongoDomainPlanning>().LoadZoneAsync);
        services.AddScoped<LoadHostnameBindingForPlanning>(provider =>
            provider.GetRequiredService<MongoDomainPlanning>().LoadBindingAsync);
        services.AddScoped<FenceDomainZone>(provider =>
            provider.GetRequiredService<MongoDomainPlanning>().FenceZoneAsync);
        services.AddScoped<FenceHostnameBinding>(provider =>
            provider.GetRequiredService<MongoDomainPlanning>().FenceBindingAsync);
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
        services.AddScoped<GetOrCreateGeneratedDomainZones>(provider =>
            provider.GetRequiredService<MongoGetOrCreateGeneratedDomainZones>().InvokeAsync);
        services.AddScoped<GetOrCreateGeneratedDomainZone>(provider =>
            async (request, session, cancellationToken) =>
            {
                var result = await provider
                    .GetRequiredService<MongoGetOrCreateGeneratedDomainZones>()
                    .InvokeAsync([request], session, cancellationToken);
                return result switch
                {
                    IReadOnlyList<TenantDomainZoneSnapshot> zones => zones[0],
                    GeneratedDomainZoneError error => error
                };
            });

        services.AddScoped<MongoLoadHostnameBindingsForDeployment>();
        services.AddScoped<MongoLoadDomainZoneBindingPage>();
        services.AddScoped<LoadDomainZoneBindingPage>(provider =>
            provider.GetRequiredService<MongoLoadDomainZoneBindingPage>().InvokeAsync);
        services.AddScoped<LoadHostnameBindingsForDeployment>(provider =>
            provider.GetRequiredService<MongoLoadHostnameBindingsForDeployment>().InvokeAsync);
        services.AddScoped<MongoLoadHostnameBindingsForDeploymentInTransaction>();
        services.AddScoped<LoadHostnameBindingsForDeploymentInTransaction>(provider =>
            provider.GetRequiredService<MongoLoadHostnameBindingsForDeploymentInTransaction>()
                .InvokeAsync);

        services.AddScoped<MongoLoadHostnameBindingsForResourceInTransaction>();
        services.AddScoped<LoadHostnameBindingsForResourceInTransaction>(provider =>
            provider.GetRequiredService<MongoLoadHostnameBindingsForResourceInTransaction>().InvokeAsync);

        services.AddScoped<MongoHostnameBindingCleanup>();
        services.AddScoped<MongoCreateGeneratedHostnameBindings>();
        services.AddScoped<CreateGeneratedHostnameBindings>(provider =>
            provider.GetRequiredService<MongoCreateGeneratedHostnameBindings>().InvokeAsync);
        services.AddScoped<CreateGeneratedHostnameBinding>(provider =>
            async (request, session, cancellationToken) =>
            {
                var result = await provider
                    .GetRequiredService<MongoCreateGeneratedHostnameBindings>()
                    .InvokeAsync([request], session, cancellationToken);
                return result switch
                {
                    IReadOnlyList<HostnameBindingSnapshot> bindings => bindings[0],
                    GeneratedHostnameBindingError error => error
                };
            });

        services.AddScoped<MongoCreateCustomHostnameBinding>();
        services.AddScoped<CreateCustomHostnameBinding>(provider =>
            provider.GetRequiredService<MongoCreateCustomHostnameBinding>().InvokeAsync);
        services.AddScoped<MongoDeleteCustomHostnameBinding>();
        services.AddScoped<DeleteCustomHostnameBinding>(provider =>
            provider.GetRequiredService<MongoDeleteCustomHostnameBinding>().InvokeAsync);

        services.AddScoped<MongoDeleteHostnameBindingsForDeployment>();
        services.AddScoped<DeleteHostnameBindingsForDeployment>(provider =>
            provider.GetRequiredService<MongoDeleteHostnameBindingsForDeployment>().InvokeAsync);
        services.AddScoped<MongoDeleteHostnameBindingsForResource>();
        services.AddScoped<DeleteHostnameBindingsForResource>(provider =>
            provider.GetRequiredService<MongoDeleteHostnameBindingsForResource>().InvokeAsync);
        services.TryAddSingleton<RunMongoOutcomeTransaction<
            CreateDomainZoneOutcome>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<
                    CreateDomainZoneOutcome>);
        services.TryAddSingleton<RunMongoOutcomeTransaction<
            DeleteDomainZoneOutcome>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<DeleteDomainZoneOutcome>);
        return services;
    }
}
