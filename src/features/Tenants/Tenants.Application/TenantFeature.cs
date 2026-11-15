using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Realtime.Contracts;
using Pole.Features.Tenants.Application.Endpoints.Invitations;
using Pole.Features.Tenants.Application.Identity;
using Pole.Features.Tenants.Application.Invitations;
using Pole.Features.Tenants.Application.Members;
using Pole.Features.Tenants.Application.Tenants;
using Pole.Features.Tenants.Contracts;
using Pole.Features.Tenants.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application;

public static class TenantFeature
{
    public static IServiceCollection AddTenantsFeature(this IServiceCollection services)
    {
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<Tenant>("Tenants"));
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<TenantInvitation>("TenantInvitations"));
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<TenantInvitationAcceptance>("TenantInvitationAcceptances"));
        services.TryAddScoped<MongoPersistTenantInvitation>();
        services.TryAddScoped<PersistTenantInvitation>(provider => provider
            .GetRequiredService<MongoPersistTenantInvitation>()
            .InvokeAsync);
        services.TryAddScoped<MongoAcceptTenantInvitation>();
        services.TryAddScoped<PersistAcceptedTenantInvitation>(provider => provider
            .GetRequiredService<MongoAcceptTenantInvitation>()
            .InvokeAsync);
        services.AddOptions<InvitationOptions>().BindConfiguration("Invitations");
        services.TryAddSingleton<InvitationLinks>();
        services.TryAddScoped<MongoLoadInvitation>();
        services.TryAddScoped<ManageInvitation>();
        services.AddSingleton(new RealtimeTopic("tenant-members", RealtimeScope.Tenant));
        services.TryAddScoped<StartInvitationOnboarding>();
        services.AddOptions<InvitationSmtpOptions>().BindConfiguration("Invitations:Smtp");
        services.TryAddScoped<SmtpInvitationMail>();
        services.TryAddScoped<SendInvitationMail>(provider =>
            provider.GetRequiredService<SmtpInvitationMail>().SendAsync);
        services.TryAddScoped<DeliverTenantInvitation>();
        services.AddHttpContextAccessor();
        services.AddSingleton(new RealtimeTopic("tenant-access", RealtimeScope.User));
        services.AddOutcomeTransaction<PersistRemovedTenantMemberOutcome>();
        services.AddOutcomeTransaction<PersistUpdatedTenantMemberRoleOutcome>();
        services.AddOutcomeTransaction<Unit>();
        services.TryAddScoped<ITenantContextAccessor, TenantContextAccessor>();
        services.AddHttpProblemMappings(TenantMemberAuthorizationHttpProblemMappings.All);
        services.AddHttpProblemMappings(
            [HttpProblemMapping.For<InvitationIdentityMismatch>(InvitationHttpProblems.IdentityMismatch)]);

        services.TryAddScoped<MongoLoadTenant>();
        services.TryAddScoped<LoadTenant>(provider => provider.GetRequiredService<MongoLoadTenant>().InvokeAsync);
        services.TryAddScoped<MongoLoadTenantsForUser>();
        services.TryAddScoped<LoadTenantsForUser>(provider => provider
            .GetRequiredService<MongoLoadTenantsForUser>()
            .InvokeAsync);
        services.TryAddScoped<MongoPersistCreatedTenant>();
        services.TryAddScoped<PersistCreatedTenant>(provider => provider
            .GetRequiredService<MongoPersistCreatedTenant>()
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

    private static void AddOutcomeTransaction<TOutcome>(this IServiceCollection services)
        where TOutcome : notnull => services.TryAddScoped<RunMongoOutcomeTransaction<TOutcome>>(provider =>
        provider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync);

    /// <summary>Registers feature authorization after request validation and active-tenant authorization.</summary>
    public static IServiceCollection AddTenantFeatureAuthorizationBehaviors(this IServiceCollection services)
    {
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TenantMemberAuthorizationBehavior<,>));
        services.AddScoped<IPipelineBehavior<AcceptTenantInvitationRequest, AcceptTenantInvitationHttpOutcome>,
            InvitationRecipientAuthorizationBehavior>();
        return services;
    }
}
