using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
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
        services.TryAddScoped<PrivateAccessEndpointCatalog>();
        services.TryAddScoped<MongoLoadOwnPrivateAccessGrants>();
        services.TryAddScoped<LoadOwnPrivateAccessGrants>(provider => provider
            .GetRequiredService<MongoLoadOwnPrivateAccessGrants>().InvokeAsync);
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
        services.TryAddScoped<Pole.Features.PrivateAccess.Contracts.RecordPrivateAccessWorkerNamespace>(
            provider => provider.GetRequiredService<MongoPrivateAccessSessionAdmission>()
                .RecordWorkerNamespaceAsync);
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
        return app;
    }
}
