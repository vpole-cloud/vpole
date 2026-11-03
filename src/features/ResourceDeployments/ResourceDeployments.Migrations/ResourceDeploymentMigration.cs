using Pole.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.ResourceDeployments.Migrations;

public sealed class ResourceDeploymentMigration : IMigration
{
    public ObjectId Id => new("69d90000b213917a38c379f8");

    public string Name => "Resource deployment lifecycle";

    public string Description =>
        "Creates uniqueness, ownership, reconciliation, and recovery indexes for resource deployments.";

    public async Task Up(
        IMongoDatabase context,
        CancellationToken cancellationToken = default)
    {
        var deployments = context.GetCollection<BsonDocument>("ResourceDeployments");
        await deployments.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("PlacementKey"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "UX_ResourceDeployments_Tenant_PlacementKey"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("ResourceType")
                    .Ascending("ResourceId")
                    .Ascending("RuntimeCellId"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "UX_ResourceDeployments_Tenant_Resource_Cell"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("ResourceType")
                    .Ascending("ResourceId"),
                new CreateIndexOptions
                {
                    Name = "IX_ResourceDeployments_Tenant_Resource"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("ResourceGroupId")
                    .Ascending("State._t"),
                new CreateIndexOptions
                {
                    Name = "IX_ResourceDeployments_Tenant_ResourceGroup_State"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("IsolationScopeId")
                    .Ascending("State._t"),
                new CreateIndexOptions
                {
                    Name = "IX_ResourceDeployments_Tenant_Scope_State"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("RuntimeCellId")
                    .Ascending("State._t"),
                new CreateIndexOptions
                {
                    Name = "IX_ResourceDeployments_Tenant_RuntimeCell_State"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("State._t")
                    .Ascending("State.RetryNotBeforeUtc")
                    .Ascending("_id"),
                new CreateIndexOptions
                {
                    Name = "IX_ResourceDeployments_State_Retry"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("State._t")
                    .Ascending("State.LeaseExpiresAtUtc")
                    .Ascending("_id"),
                new CreateIndexOptions
                {
                    Name = "IX_ResourceDeployments_State_Lease"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("ResolvedPlacement.DeploymentTargetId")
                    .Ascending("State._t"),
                new CreateIndexOptions
                {
                    Name = "IX_ResourceDeployments_Target_State"
                })
        ], cancellationToken);
    }
}
