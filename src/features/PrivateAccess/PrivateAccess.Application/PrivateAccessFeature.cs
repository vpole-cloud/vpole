using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.PrivateAccess.Application.Admission;
using Pole.Features.PrivateAccess.Application.Catalog;
using Pole.Features.PrivateAccess.Application.Endpoints;
using Pole.Features.PrivateAccess.Application.Gateway;
using Pole.Features.PrivateAccess.Application.Grants;
using Pole.Features.PrivateAccess.Application.Sessions;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application;

public static class PrivateAccessFeature
{
    public static IServiceCollection AddPrivateAccessFeature(this IServiceCollection services)
    {
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<PrivateAccessGrant>("PrivateAccessGrants"));
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<PrivateAccessSession>("PrivateAccessSessions"));
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<PrivateAccessAudit>("PrivateAccessAudit"));
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<PrivateAccessGateway>("PrivateAccessGateways"));
        services.TryAddSingleton(provider => provider.GetRequiredService<IMongoDatabase>()
            .GetCollection<PrivateAccessBrokerOwner>("PrivateAccessBrokerOwners"));
        services.TryAddScoped<MongoPrivateAccessBrokerOwnership>();
        services.TryAddScoped<ClaimPrivateAccessBrokerOwner>(provider =>
            provider.GetRequiredService<MongoPrivateAccessBrokerOwnership>().ClaimAsync);
        services.TryAddScoped<RenewPrivateAccessBrokerOwner>(provider =>
            provider.GetRequiredService<MongoPrivateAccessBrokerOwnership>().RenewAsync);
        services.TryAddScoped<ReleasePrivateAccessBrokerOwner>(provider =>
            provider.GetRequiredService<MongoPrivateAccessBrokerOwnership>().ReleaseAsync);
        services.TryAddScoped<LoadPrivateAccessBrokerOwner>(provider =>
            provider.GetRequiredService<MongoPrivateAccessBrokerOwnership>().LoadAsync);
        services.TryAddScoped<PrivateAccessGatewayIdentityReads>();
        services.TryAddScoped<MongoLoadPrivateAccessGatewayObservation>();
        services.TryAddScoped<LoadPrivateAccessGatewayObservation>(provider => provider
            .GetRequiredService<MongoLoadPrivateAccessGatewayObservation>()
            .InvokeAsync);
        services.TryAddScoped<ResolvePrivateAccessGatewayIdentity>(provider =>
            provider.GetRequiredService<PrivateAccessGatewayIdentityReads>().ResolveAsync);
        services.TryAddScoped<RecordPrivateAccessGatewayContact>(provider =>
            provider.GetRequiredService<PrivateAccessGatewayIdentityReads>().RecordContactAsync);
        services.TryAddScoped<PrivateAccessGatewayDesiredStates>();
        services.TryAddScoped<LoadPrivateAccessGatewayDesiredStates>(provider =>
            provider.GetRequiredService<PrivateAccessGatewayDesiredStates>().LoadAsync);
        services.TryAddScoped<LoadPrivateAccessGatewayDesiredStatesForTenant>(provider =>
            provider.GetRequiredService<PrivateAccessGatewayDesiredStates>().LoadForTenantAsync);
        services.TryAddScoped<PrivateAccessGatewayRegistrations>();
        services.TryAddScoped<LoadPrivateAccessGatewayRegistrations>(provider =>
            provider.GetRequiredService<PrivateAccessGatewayRegistrations>().LoadAsync);
        services.TryAddScoped<SavePrivateAccessGatewayRegistration>(provider =>
            provider.GetRequiredService<PrivateAccessGatewayRegistrations>().SaveAsync);
        services.TryAddScoped<RemovePrivateAccessGatewayRegistration>(provider =>
            provider.GetRequiredService<PrivateAccessGatewayRegistrations>().RemoveAsync);
        services.TryAddScoped<PrivateAccessEndpointCatalog>();
        services.TryAddScoped<PrivateAccessGrantRoutes>();
        services.TryAddScoped<LoadPrivateAccessGrantRoutes>(provider => provider
            .GetRequiredService<PrivateAccessGrantRoutes>()
            .LoadAsync);
        services.TryAddScoped<PrivateAccessResourceNameCatalog>();
        services.TryAddScoped<MongoLoadOwnPrivateAccessGrants>();
        services.TryAddScoped<LoadOwnPrivateAccessGrants>(provider => provider
            .GetRequiredService<MongoLoadOwnPrivateAccessGrants>()
            .InvokeAsync);
        services.TryAddScoped<LoadOwnPrivateAccessResourceGrants>(provider => provider
            .GetRequiredService<MongoLoadOwnPrivateAccessGrants>()
            .LoadResourceAsync);
        services.TryAddScoped<LoadTenantPrivateAccessGrants>(provider => provider
            .GetRequiredService<MongoLoadOwnPrivateAccessGrants>()
            .LoadTenantAsync);
        services.TryAddScoped<LoadPrivateAccessGrant>(provider => provider
            .GetRequiredService<MongoLoadOwnPrivateAccessGrants>()
            .LoadOneAsync);
        services.TryAddScoped<PrivateAccessAdmission>();
        services.TryAddScoped<MongoCreatePrivateAccessGrant>();
        services.TryAddScoped<RunMongoOutcomeTransaction<CreatePrivateAccessGrantPersistenceOutcome>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync);
        services.TryAddScoped<PersistPrivateAccessGrant>(provider => provider
            .GetRequiredService<MongoCreatePrivateAccessGrant>()
            .InvokeAsync);
        services.TryAddScoped<MongoRevokePrivateAccessGrant>();
        services.TryAddScoped<RunMongoOutcomeTransaction<PersistRevokePrivateAccessGrantOutcome>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync);
        services.TryAddScoped<RevokePrivateAccessGrant>(provider => provider
            .GetRequiredService<MongoRevokePrivateAccessGrant>()
            .InvokeAsync);
        services.TryAddScoped<MongoIssuePrivateAccessSession>();
        services.TryAddScoped<RunMongoOutcomeTransaction<IssuePrivateAccessSessionOutcome>>(provider =>
            provider.GetRequiredService<MongoRunTransaction>().InvokeOutcomeAsync);
        services.TryAddScoped<IssuePrivateAccessSession>(provider => provider
            .GetRequiredService<MongoIssuePrivateAccessSession>()
            .InvokeAsync);
        services.TryAddScoped<MongoPrivateAccessSessionAdmission>();
        services.TryAddScoped<LocatePrivateAccessTicket>(provider =>
            provider.GetRequiredService<MongoPrivateAccessSessionAdmission>().LocateAsync);
        services.TryAddScoped<RedeemPrivateAccessSession>(provider =>
            provider.GetRequiredService<MongoPrivateAccessSessionAdmission>().RedeemAsync);
        services.TryAddScoped<RenewPrivateAccessSession>(provider =>
            provider.GetRequiredService<MongoPrivateAccessSessionAdmission>().RenewAsync);
        services.TryAddScoped<MarkPrivateAccessSessionStarted>(provider =>
            provider.GetRequiredService<MongoPrivateAccessSessionAdmission>().MarkStartedAsync);
        services.TryAddScoped<ClosePrivateAccessSession>(provider =>
            provider.GetRequiredService<MongoPrivateAccessSessionAdmission>().CloseAsync);
        services.TryAddScoped<MongoPrivateAccessSessionCleanup>();
        services.TryAddScoped<LoadExpiredPrivateAccessSessionCleanupPage>(provider =>
            provider.GetRequiredService<MongoPrivateAccessSessionCleanup>().LoadPageAsync);
        services.TryAddScoped<MarkPrivateAccessSessionCleaned>(provider =>
            provider.GetRequiredService<MongoPrivateAccessSessionCleanup>().MarkCleanedAsync);
        return services;
    }

    public static IEndpointRouteBuilder MapPrivateAccessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/private-access/grants")
            .WithTags("PrivateAccess")
            .MapCreatePrivateAccessGrantEndpoint()
            .MapListPrivateAccessGrantsEndpoint()
            .MapManagePrivateAccessGrantsEndpoint()
            .MapGetPrivateAccessGrantEndpoint()
            .MapRevokePrivateAccessGrantEndpoint()
            .MapCreatePrivateAccessSessionEndpoint();
        app.MapGroup("/private-access/resources")
            .WithTags("PrivateAccess")
            .MapListPrivateAccessResourceGrantsEndpoint();
        app.MapGroup("/platform/private-access/gateways")
            .WithoutTenantContext()
            .WithTags("PlatformPrivateAccess")
            .RequireAuthorization(Policy.Admin)
            .MapGetPrivateAccessGatewayStatusEndpoint();
        return app;
    }
}
