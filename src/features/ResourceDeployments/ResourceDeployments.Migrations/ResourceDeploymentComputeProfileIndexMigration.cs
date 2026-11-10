using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.ResourceDeployments.Migrations;

public sealed class ResourceDeploymentComputeProfileIndexMigration : IMigration
{
    public ObjectId Id => new("6a700005b213917a38c38005");

    public string Name => "Resource deployment compute profile references";

    public string Description => "Indexes resolved compute profile references used by compute profile deletion safety.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var deployments = context.GetCollection<BsonDocument>("ResourceDeployments");
        return deployments.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("ResolvedPlacement.Compute.ComputeProfileId"),
                new CreateIndexOptions
                {
                    Name = "IX_ResourceDeployments_ComputeProfile",
                }),
            cancellationToken: cancellationToken);
    }
}
