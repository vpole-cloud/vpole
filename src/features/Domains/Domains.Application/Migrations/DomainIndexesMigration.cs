using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Domains.Application.Persistence;

namespace Pole.Features.Domains.Application.Migrations;

public sealed class DomainIndexesMigration : IMigration
{
    public ObjectId Id => new("6a900003b213917a38c38103");
    public string Name => "Tenant domain-zone and hostname-binding indexes";
    public string Description => "Creates uniqueness, listing, and target-reference guard indexes for domains.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var zones = context.GetCollection<BsonDocument>(DomainCollectionNames.TenantDomainZones);
        await zones.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Identity.TechnicalName"),
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    Name = DomainIndexNames.ZoneTechnicalName,
                    PartialFilterExpression = DomainIdentityFilters.Named<BsonDocument>()
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("DeploymentTargetId").Ascending("Domain"),
                new CreateIndexOptions { Unique = true, Name = DomainIndexNames.ZoneTargetDomain }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("DeploymentTargetId")
                    .Ascending(DomainIdentityFilters.Discriminator),
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    Name = "UX_TenantDomainZones_Tenant_Target_Generated",
                    PartialFilterExpression = DomainIdentityFilters.Generated<BsonDocument>()
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("DeploymentTargetId").Ascending("Domain").Ascending("_id"),
                new CreateIndexOptions { Name = "IX_TenantDomainZones_Tenant_Target_Domain" })
        ], cancellationToken);

        var bindings = context.GetCollection<BsonDocument>(DomainCollectionNames.HostnameBindings);
        await bindings.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Identity.TechnicalName"),
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    Name = DomainIndexNames.BindingTechnicalName,
                    PartialFilterExpression = DomainIdentityFilters.Named<BsonDocument>()
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("Hostname"),
                new CreateIndexOptions { Unique = true, Name = DomainIndexNames.BindingHostname }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("ResourceDeploymentId")
                    .Ascending(DomainIdentityFilters.Discriminator),
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    Name = "UX_HostnameBindings_Tenant_Deployment_Generated",
                    PartialFilterExpression = DomainIdentityFilters.Generated<BsonDocument>()
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("ResourceType").Ascending("ResourceId").Ascending("ResourceDeploymentId"),
                new CreateIndexOptions { Name = "IX_HostnameBindings_Tenant_Resource_Deployment" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("TenantDomainZoneId"),
                new CreateIndexOptions { Name = "IX_HostnameBindings_Zone" }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("DeploymentTargetId"),
                new CreateIndexOptions { Name = "IX_HostnameBindings_Target" })
        ], cancellationToken);
    }
}
