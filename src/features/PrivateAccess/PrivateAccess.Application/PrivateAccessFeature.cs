using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.PrivateAccess.Application.Endpoints;
using Pole.Features.PrivateAccess.Application.Persistence;
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
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.ClaimPrivateAccessBrokerOwner>(
            provider => provider.GetRequiredService<MongoPrivateAccessBrokerOwnership>().ClaimAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.RenewPrivateAccessBrokerOwner>(
            provider => provider.GetRequiredService<MongoPrivateAccessBrokerOwnership>().RenewAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.ReleasePrivateAccessBrokerOwner>(
            provider => provider.GetRequiredService<MongoPrivateAccessBrokerOwnership>().ReleaseAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.LoadPrivateAccessBrokerOwner>(
            provider => provider.GetRequiredService<MongoPrivateAccessBrokerOwnership>().LoadAsync);
        services.TryAddScoped<PrivateAccessGatewayIdentityReads>();
        services.TryAddScoped<MongoLoadPrivateAccessGatewayObservation>();
        services.TryAddScoped<LoadPrivateAccessGatewayObservation>(provider => provider
            .GetRequiredService<MongoLoadPrivateAccessGatewayObservation>().InvokeAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.ResolvePrivateAccessGatewayIdentity>(
            provider => provider.GetRequiredService<PrivateAccessGatewayIdentityReads>().ResolveAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.RecordPrivateAccessGatewayContact>(
            provider => provider.GetRequiredService<PrivateAccessGatewayIdentityReads>().RecordContactAsync);
        services.TryAddScoped<PrivateAccessGatewayDesiredStates>();
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.LoadPrivateAccessGatewayDesiredStates>(
            provider => provider.GetRequiredService<PrivateAccessGatewayDesiredStates>().LoadAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.LoadPrivateAccessGatewayDesiredStatesForTenant>(
            provider => provider.GetRequiredService<PrivateAccessGatewayDesiredStates>().LoadForTenantAsync);
        services.TryAddScoped<PrivateAccessGatewayRegistrations>();
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.LoadPrivateAccessGatewayRegistrations>(
            provider => provider.GetRequiredService<PrivateAccessGatewayRegistrations>().LoadAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.SavePrivateAccessGatewayRegistration>(
            provider => provider.GetRequiredService<PrivateAccessGatewayRegistrations>().SaveAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.RemovePrivateAccessGatewayRegistration>(
            provider => provider.GetRequiredService<PrivateAccessGatewayRegistrations>().RemoveAsync);
        services.TryAddScoped<PrivateAccessEndpointCatalog>();
        services.TryAddScoped<PrivateAccessGrantRoutes>();
        services.TryAddScoped<LoadPrivateAccessGrantRoutes>(provider => provider
            .GetRequiredService<PrivateAccessGrantRoutes>().LoadAsync);
        services.TryAddScoped<PrivateAccessResourceNameCatalog>();
        services.TryAddScoped<MongoLoadOwnPrivateAccessGrants>();
        services.TryAddScoped<LoadOwnPrivateAccessGrants>(provider => provider
            .GetRequiredService<MongoLoadOwnPrivateAccessGrants>().InvokeAsync);
        services.TryAddScoped<LoadOwnPrivateAccessResourceGrants>(provider => provider
            .GetRequiredService<MongoLoadOwnPrivateAccessGrants>().LoadResourceAsync);
        services.TryAddScoped<LoadTenantPrivateAccessGrants>(provider => provider
            .GetRequiredService<MongoLoadOwnPrivateAccessGrants>().LoadTenantAsync);
        services.TryAddScoped<LoadPrivateAccessGrant>(provider => provider
            .GetRequiredService<MongoLoadOwnPrivateAccessGrants>().LoadOneAsync);
        services.TryAddScoped<PrivateAccessAdmission>();
        services.TryAddScoped<MongoCreatePrivateAccessGrant>();
        services.TryAddScoped<RunMongoOutcomeTransaction<CreatePrivateAccessGrantPersistenceOutcome>>(
            provider => provider.GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<CreatePrivateAccessGrantPersistenceOutcome>);
        services.TryAddScoped<PersistPrivateAccessGrant>(provider => provider
            .GetRequiredService<MongoCreatePrivateAccessGrant>().InvokeAsync);
        services.TryAddScoped<MongoRevokePrivateAccessGrant>();
        services.TryAddScoped<RunMongoOutcomeTransaction<PersistRevokePrivateAccessGrantOutcome>>(
            provider => provider.GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<PersistRevokePrivateAccessGrantOutcome>);
        services.TryAddScoped<RevokePrivateAccessGrant>(provider => provider
            .GetRequiredService<MongoRevokePrivateAccessGrant>().InvokeAsync);
        services.TryAddScoped<MongoIssuePrivateAccessSession>();
        services.TryAddScoped<RunMongoOutcomeTransaction<IssuePrivateAccessSessionOutcome>>(
            provider => provider.GetRequiredService<MongoRunTransaction>()
                .InvokeOutcomeAsync<IssuePrivateAccessSessionOutcome>);
        services.TryAddScoped<IssuePrivateAccessSession>(provider => provider
            .GetRequiredService<MongoIssuePrivateAccessSession>().InvokeAsync);
        services.TryAddScoped<MongoPrivateAccessSessionAdmission>();
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.LocatePrivateAccessTicket>(provider =>
            provider.GetRequiredService<MongoPrivateAccessSessionAdmission>().LocateAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.RedeemPrivateAccessSession>(
            provider => provider.GetRequiredService<MongoPrivateAccessSessionAdmission>()
                .RedeemAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.RenewPrivateAccessSession>(
            provider => provider.GetRequiredService<MongoPrivateAccessSessionAdmission>()
                .RenewAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.MarkPrivateAccessSessionStarted>(
            provider => provider.GetRequiredService<MongoPrivateAccessSessionAdmission>()
                .MarkStartedAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.ClosePrivateAccessSession>(
            provider => provider.GetRequiredService<MongoPrivateAccessSessionAdmission>()
                .CloseAsync);
        services.TryAddScoped<MongoPrivateAccessSessionCleanup>();
        services.TryAddScoped<
            Pole.Features.PrivateAccess.Contracts.LoadExpiredPrivateAccessSessionCleanupPage>(
            provider => provider.GetRequiredService<MongoPrivateAccessSessionCleanup>()
                .LoadPageAsync);
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.MarkPrivateAccessSessionCleaned>(
            provider => provider.GetRequiredService<MongoPrivateAccessSessionCleanup>()
                .MarkCleanedAsync);
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
