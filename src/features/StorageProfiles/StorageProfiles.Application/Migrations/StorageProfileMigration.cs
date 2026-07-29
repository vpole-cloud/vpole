using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.StorageProfiles.Application.Migrations;

public sealed class StorageProfileMigration : IMigration
{
    public ObjectId Id => new("69da0000b213917a38c379f9");

    public string Name => "Target-aware storage profiles";

    public string Description =>
        "Creates the single-default and target-aware storage profile indexes.";

    public async Task Up(
        IMongoDatabase context,
        CancellationToken cancellationToken = default)
    {
        var profiles = context.GetCollection<BsonDocument>("StorageProfiles");
        await profiles.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("IsDefault"),
                new CreateIndexOptions<BsonDocument>
                {
                    Unique = true,
                    PartialFilterExpression = new BsonDocument("IsDefault", true),
                    Name = "UX_StorageProfiles_Default"
                }),
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TargetMappings.DeploymentTargetId")
                    .Ascending("IsEnabled")
                    .Descending("IsDefault")
                    .Ascending("DisplayName"),
                new CreateIndexOptions
                {
                    Name = "IX_StorageProfiles_Target_Availability"
                })
        ], cancellationToken);
    }
}
