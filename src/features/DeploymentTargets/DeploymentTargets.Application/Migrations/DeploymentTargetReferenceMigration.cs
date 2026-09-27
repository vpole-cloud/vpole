using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.DeploymentTargets.Application.Migrations;

public sealed class DeploymentTargetReferenceMigration : IMigration
{
    public ObjectId Id => new("69d60000b213917a38c379f5");
    public string Name => "Deployment target reference registry";
    public string Description => "Creates lookup and unique owner indexes for durable deployment-target references.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var references = context.GetCollection<BsonDocument>("DeploymentTargetReferences");
        await references.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("DeploymentTargetId"),
                new CreateIndexOptions
                {
                    Name = "IX_DeploymentTargetReferences_DeploymentTargetId"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("DeploymentTargetId")
                    .Ascending("OwnerType")
                    .Ascending("OwnerId"),
                new CreateIndexOptions
                {
                    Unique = true,
                    Name = "UX_DeploymentTargetReferences_Target_Owner"
                })
        ], cancellationToken);
    }
}
