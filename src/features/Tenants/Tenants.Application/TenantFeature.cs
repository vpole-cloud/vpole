using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pole.ASP.Helpers;
using Pole.Database;
using MongoDB.Driver;
using Pole.Features.Realtime.Contracts;
using Pole.ResultType;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Persistence;
using Pole.Features.Tenants.Application.Persistence.Identity;
using Pole.Features.Tenants.Application.Persistence.Members;
using Pole.Features.Tenants.Application.Persistence.Tenants;
using Pole.Features.Tenants.Contracts;
using Pole.Features.Tenants.Domain;
using Pole.Mediator;

namespace Pole.Features.Tenants.Application;

public static class TenantFeature
{
    public static IServiceCollection AddTenantsFeature(this IServiceCollection services)
    {
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<Tenant>("Tenants"));
        services.AddHttpContextAccessor();
        services.AddSingleton(new RealtimeTopic("tenant-access", RealtimeScope.User));
        services.AddOutcomeTransaction<PersistAddedTenantMemberOutcome>();
        services.AddOutcomeTransaction<PersistRemovedTenantMemberOutcome>();
        services.AddOutcomeTransaction<PersistUpdatedTenantMemberRoleOutcome>();
        services.AddOutcomeTransaction<Unit>();
        services.TryAddScoped<ITenantContextAccessor, TenantContextAccessor>();
        services.AddHttpProblemMappings(TenantMemberAuthorizationHttpProblemMappings.All);

        services.TryAddScoped<MongoLoadTenant>();
        services.TryAddScoped<LoadTenant>(provider => provider
            .GetRequiredService<MongoLoadTenant>()
            .InvokeAsync);
        services.TryAddScoped<MongoLoadTenantsForUser>();
        services.TryAddScoped<LoadTenantsForUser>(provider => provider
            .GetRequiredService<MongoLoadTenantsForUser>()
            .InvokeAsync);
        services.TryAddScoped<MongoPersistCreatedTenant>();
        services.TryAddScoped<PersistCreatedTenant>(provider => provider
            .GetRequiredService<MongoPersistCreatedTenant>()
            .InvokeAsync);
        services.TryAddScoped<MongoPersistAddedTenantMember>();
        services.TryAddScoped<PersistAddedTenantMember>(provider => provider
            .GetRequiredService<MongoPersistAddedTenantMember>()
            .InvokeAsync);
        services.TryAddScoped<MongoPersistUpdatedTenantMemberRole>();
        services.TryAddScoped<PersistUpdatedTenantMemberRole>(provider => provider
            .GetRequiredService<MongoPersistUpdatedTenantMemberRole>()
            .InvokeAsync);
        services.TryAddScoped<MongoPersistRemovedTenantMember>();
        services.TryAddScoped<PersistRemovedTenantMember>(provider => provider
            .GetRequiredService<MongoPersistRemovedTenantMember>()
            .InvokeAsync);
        services.TryAddScoped<MongoEnsureDefaultTenant>();
        services.TryAddScoped<EnsureDefaultTenant>(provider => provider
            .GetRequiredService<MongoEnsureDefaultTenant>()
            .InvokeAsync);
        services.TryAddScoped<MongoLoadTenantIdentity>();
        services.TryAddScoped<LoadTenantIdentity>(provider => provider
            .GetRequiredService<MongoLoadTenantIdentity>()
            .InvokeAsync);
        services.TryAddScoped<MongoFenceTenantIdentity>();
        services.TryAddScoped<FenceTenantIdentity>(provider => provider
            .GetRequiredService<MongoFenceTenantIdentity>()
            .InvokeAsync);
        services.TryAddScoped<MongoLoadTenantMember>();
        services.TryAddScoped<LoadTenantMember>(provider => provider
            .GetRequiredService<MongoLoadTenantMember>()
            .InvokeAsync);
        services.TryAddScoped<MongoEnsureTenantsExist>();
        services.TryAddScoped<EnsureTenantsExist>(provider => provider
            .GetRequiredService<MongoEnsureTenantsExist>()
            .InvokeAsync);

        return services;
    }

    private static void AddOutcomeTransaction<TOutcome>(this IServiceCollection services) where TOutcome : notnull =>
        services.TryAddScoped<RunMongoOutcomeTransaction<TOutcome>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync<TOutcome>);

    /// <summary>
    /// Adds the tenant-member authorization stage after the common mediator stages.
    /// The composition root invokes this separately so its position in the pipeline
    /// remains validation, tenant authorization, then tenant-member authorization.
    /// </summary>
    public static IServiceCollection AddTenantMemberAuthorizationBehavior(
        this IServiceCollection services)
    {
        services.AddScoped(
            typeof(IPipelineBehavior<,>),
            typeof(TenantMemberAuthorizationBehavior<,>));
        return services;
    }
}
