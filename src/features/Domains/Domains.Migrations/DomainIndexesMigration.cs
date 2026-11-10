using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Domains.Migrations;

public sealed class DomainIndexesMigration : IMigration
{
    public ObjectId Id => new("6a900003b213917a38c38103");
    public string Name => "Tenant domain-zone and hostname-binding indexes";
    public string Description => "Creates uniqueness, listing, and target-reference guard indexes for domains.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var zones = context.GetCollection<BsonDocument>("TenantDomainZones");
        await zones.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Identity.TechnicalName"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Unique = true,
                        Name = "UX_TenantDomainZones_Tenant_TechnicalName",
                        PartialFilterExpression = new BsonDocument("Identity._t", "NamedDomainIdentity"),
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("DeploymentTargetId").Ascending("Domain"),
                    new CreateIndexOptions { Unique = true, Name = "UX_TenantDomainZones_Target_Domain" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("DeploymentTargetId")
                        .Ascending("Identity._t"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Unique = true,
                        Name = "UX_TenantDomainZones_Tenant_Target_Generated",
                        PartialFilterExpression = new BsonDocument("Identity._t", "GeneratedDomainIdentity"),
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("DeploymentTargetId")
                        .Ascending("Domain")
                        .Ascending("_id"),
                    new CreateIndexOptions { Name = "IX_TenantDomainZones_Tenant_Target_Domain" }),
            ],
            cancellationToken);

        var bindings = context.GetCollection<BsonDocument>("HostnameBindings");
        await bindings.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Identity.TechnicalName"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Unique = true,
                        Name = "UX_HostnameBindings_Tenant_TechnicalName",
                        PartialFilterExpression = new BsonDocument("Identity._t", "NamedDomainIdentity"),
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("Hostname"),
                    new CreateIndexOptions { Unique = true, Name = "UX_HostnameBindings_Hostname" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("ResourceDeploymentId")
                        .Ascending("Identity._t"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Unique = true,
                        Name = "UX_HostnameBindings_Tenant_Deployment_Generated",
                        PartialFilterExpression = new BsonDocument("Identity._t", "GeneratedDomainIdentity"),
                    }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys
                        .Ascending("TenantId")
                        .Ascending("ResourceType")
                        .Ascending("ResourceId")
                        .Ascending("ResourceDeploymentId"),
                    new CreateIndexOptions { Name = "IX_HostnameBindings_Tenant_Resource_Deployment" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantDomainZoneId"),
                    new CreateIndexOptions { Name = "IX_HostnameBindings_Zone" }),
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("DeploymentTargetId"),
                    new CreateIndexOptions { Name = "IX_HostnameBindings_Target" }),
            ],
            cancellationToken);
    }
}
