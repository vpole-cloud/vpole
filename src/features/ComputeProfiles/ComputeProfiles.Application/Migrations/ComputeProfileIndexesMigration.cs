using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.ComputeProfiles.Application.Migrations;

public sealed class ComputeProfileIndexesMigration : IMigration
{
    public ObjectId Id => new("6a700004b213917a38c38004");

    public string Name => "Compute profile indexes";

    public string Description =>
        "Creates default uniqueness, administration list, and target availability indexes for compute profiles.";

    public Task Up(
        IMongoDatabase context,
        CancellationToken cancellationToken = default)
    {
        var profiles = context.GetCollection<BsonDocument>("ComputeProfiles");
        return profiles.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("IsDefault"),
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    PartialFilterExpression = new BsonDocument("IsDefault", true),
                    Name = "UX_ComputeProfiles_Default"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Descending("IsDefault")
                    .Ascending("DisplayName")
                    .Ascending("_id"),
                new CreateIndexOptions
                {
                    Name = "IX_ComputeProfiles_List"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("IsEnabled")
                    .Ascending("TargetMappings.DeploymentTargetId")
                    .Descending("IsDefault")
                    .Ascending("DisplayName")
                    .Ascending("_id"),
                new CreateIndexOptions
                {
                    Name = "IX_ComputeProfiles_Target_Availability"
                })
        ], cancellationToken);
    }
}
