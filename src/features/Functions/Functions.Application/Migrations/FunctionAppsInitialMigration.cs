using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Migrations;

public sealed class FunctionAppsInitialMigration : IMigration
{
    public ObjectId Id => new("69d50000b213917a38c379f0");
    public string Name => "Function Apps initial indexes";
    public string Description => "Creates tenant, immutable revision, active revision, and global route indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var apps = context.GetCollection<FunctionApp>("FunctionApps");
        var revisions = context.GetCollection<FunctionRevision>("FunctionRevisions");
        var routes = context.GetCollection<FunctionRouteBinding>("FunctionRouteBindings");

        await apps.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<FunctionApp>(
                Builders<FunctionApp>.IndexKeys
                    .Ascending(app => app.TenantId)
                    .Descending(app => app.CreatedAtUtc),
                new CreateIndexOptions { Name = "IX_FunctionApps_Tenant_CreatedAt" }),
            new CreateIndexModel<FunctionApp>(
                Builders<FunctionApp>.IndexKeys
                    .Ascending(app => app.TenantId)
                    .Ascending(app => app.ResourceGroupId),
                new CreateIndexOptions { Name = "IX_FunctionApps_Tenant_ResourceGroup" }),
            new CreateIndexModel<FunctionApp>(
                Builders<FunctionApp>.IndexKeys
                    .Ascending(app => app.TenantId)
                    .Ascending(app => app.NormalizedName),
                new CreateIndexOptions
                {
                    Name = "UX_FunctionApps_Tenant_NormalizedName",
                    Unique = true
                })
        ], cancellationToken);

        await revisions.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<FunctionRevision>(
                Builders<FunctionRevision>.IndexKeys
                    .Ascending(revision => revision.TenantId)
                    .Ascending(revision => revision.FunctionAppId)
                    .Ascending(revision => revision.RevisionNumber),
                new CreateIndexOptions
                {
                    Name = "UX_FunctionRevisions_Tenant_App_Revision",
                    Unique = true
                }),
            new CreateIndexModel<FunctionRevision>(
                Builders<FunctionRevision>.IndexKeys
                    .Ascending(revision => revision.TenantId)
                    .Ascending(revision => revision.FunctionAppId)
                    .Descending(revision => revision.CreatedAtUtc),
                new CreateIndexOptions { Name = "IX_FunctionRevisions_Tenant_App_CreatedAt" }),
            new CreateIndexModel<FunctionRevision>(
                Builders<FunctionRevision>.IndexKeys
                    .Ascending(revision => revision.TenantId)
                    .Ascending(revision => revision.FunctionAppId),
                new CreateIndexOptions<FunctionRevision>
                {
                    Name = "UX_FunctionRevisions_Tenant_App_Active",
                    Unique = true,
                    PartialFilterExpression = Builders<FunctionRevision>.Filter.Eq(
                        "Status._t",
                        "active")
                })
        ], cancellationToken);

        await routes.Indexes.CreateOneAsync(
            new CreateIndexModel<FunctionRouteBinding>(
                Builders<FunctionRouteBinding>.IndexKeys
                    .Ascending(route => route.TenantId)
                    .Ascending(route => route.FunctionAppId),
                new CreateIndexOptions
                {
                    Name = "UX_FunctionRouteBindings_Tenant_App",
                    Unique = true
                }),
            cancellationToken: cancellationToken);
    }
}
