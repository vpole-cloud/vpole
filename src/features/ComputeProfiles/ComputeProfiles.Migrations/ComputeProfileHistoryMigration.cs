using Pole.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.ComputeProfiles.Migrations;

public sealed class ComputeProfileHistoryMigration : IMigration
{
    public ObjectId Id => new("6b700088b213917a38c38004");
    public string Name => "Immutable compute profile revision snapshots";
    public string Description => "Retains catalog revisions for reproducible explicitly pinned workload configuration.";
    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var history = context.GetCollection<BsonDocument>("ComputeProfileRevisions");
        await history.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys
            .Ascending("ComputeProfileId").Ascending("Revision"), new CreateIndexOptions { Unique = true }),
                cancellationToken: cancellationToken);
        using var collections = await context.ListCollectionsAsync(new ListCollectionsOptions
        {
            Filter =
            new BsonDocument("name", "ComputeProfiles")
        }, cancellationToken);
        var collection = await collections.FirstAsync(cancellationToken);
        var snapshotSchema = collection["options"]["validator"]["$jsonSchema"].AsBsonDocument;
        var schema = new BsonDocument
        {
            ["bsonType"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new BsonArray { "_id", "ComputeProfileId", "Revision", "Snapshot" },
            ["properties"] = new BsonDocument
            {
                ["_id"] = new BsonDocument("bsonType", "string"),
                ["ComputeProfileId"] = new BsonDocument("bsonType", "string"),
                ["Revision"] = new BsonDocument { ["bsonType"] = "long", ["minimum"] = 1 },
                ["Snapshot"] = snapshotSchema
            }
        };
        await context.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            ["collMod"] = "ComputeProfileRevisions",
            ["validator"] = new BsonDocument("$jsonSchema", schema),
            ["validationLevel"] = "strict",
            ["validationAction"] = "error"
        }, cancellationToken: cancellationToken);
    }
}
