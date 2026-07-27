using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.ResourceGroups.Application.Migrations;

public sealed class ResourceGroupPlacementMigration : IMigration
{
    public ObjectId Id => new("69d70000b213917a38c379f6");
    public string Name => "Resource group isolation scopes and runtime cells";
    public string Description =>
        "Creates ownership, uniqueness, target lookup, and recovery indexes for isolation scopes and runtime cells.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var scopes = context.GetCollection<BsonDocument>("IsolationScopes");
        await scopes.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("ResourceGroupId")
                    .Ascending("IsDefault"),
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    Name = "UX_IsolationScopes_ResourceGroup_Default",
                    PartialFilterExpression = new BsonDocument("IsDefault", true)
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("ResourceGroupId")
                    .Ascending("NormalizedName"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "UX_IsolationScopes_ResourceGroup_Name"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("ResourceGroupId"),
                new CreateIndexOptions
                {
                    Name = "IX_IsolationScopes_Tenant_ResourceGroup"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("Lifecycle._t")
                    .Ascending("UpdatedAtUtc"),
                new CreateIndexOptions
                {
                    Name = "IX_IsolationScopes_Lifecycle_UpdatedAt"
                })
        ], cancellationToken);

        var runtimeCells = context.GetCollection<BsonDocument>("RuntimeCells");
        await runtimeCells.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("IsolationScopeId")
                    .Ascending("DeploymentTargetId"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "UX_RuntimeCells_Scope_Target"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("ResourceGroupId"),
                new CreateIndexOptions
                {
                    Name = "IX_RuntimeCells_Tenant_ResourceGroup"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("DeploymentTargetId")
                    .Ascending("State._t"),
                new CreateIndexOptions
                {
                    Name = "IX_RuntimeCells_Target_State"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("State._t")
                    .Ascending("State.RetryNotBeforeUtc"),
                new CreateIndexOptions
                {
                    Name = "IX_RuntimeCells_State_Retry"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("State._t")
                    .Ascending("UpdatedAtUtc"),
                new CreateIndexOptions
                {
                    Name = "IX_RuntimeCells_State_UpdatedAt"
                })
        ], cancellationToken);
    }
}
