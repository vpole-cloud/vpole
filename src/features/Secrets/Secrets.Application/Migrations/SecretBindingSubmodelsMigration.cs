using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.Secrets.Application.Migrations;

public sealed class SecretBindingSubmodelsMigration : IMigration
{
    public ObjectId Id => new("6b700089b213917a38c38001");
    public string Name => "Secret binding consumer and key pins";
    public string Description => "Group consumer identity and durable key pins without losing existing bindings.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var collection = context.GetCollection<BsonDocument>("Secrets");
        // Frozen BSON names describe the old schema as well as the migration target.
        PipelineDefinition<BsonDocument, BsonDocument> pipeline = new[]
        {
            BsonDocument.Parse("""
                { "$set": { "Bindings": { "$map": {
                    "input": { "$ifNull": [ "$Bindings", [] ] },
                    "as": "binding",
                    "in": { "$mergeObjects": [ "$$binding", {
                        "Consumer": { "$ifNull": [ "$$binding.Consumer", {
                            "ResourceType": "$$binding.ConsumerType", "ResourceId": "$$binding.ConsumerId"
                        } ] },
                        "Pins": { "$ifNull": [ "$$binding.Pins", {
                            "Required": { "$ifNull": [ "$$binding.RequiredKeys", [] ] },
                            "Retained": { "$ifNull": [ "$$binding.RetainedKeys", [] ] }
                        } ] }
                    } ] }
                } } } }
                """),
            BsonDocument.Parse("""
                { "$unset": [ "Bindings.ConsumerType", "Bindings.ConsumerId", "Bindings.RequiredKeys", "Bindings.RetainedKeys" ] }
                """)
        };
        await collection.UpdateManyAsync(
            FilterDefinition<BsonDocument>.Empty,
            new PipelineUpdateDefinition<BsonDocument>(pipeline),
            cancellationToken: cancellationToken);

        const string oldIndex = "TenantId_1_Bindings.ConsumerType_1_Bindings.ConsumerId_1";
        using var cursor = await collection.Indexes.ListAsync(cancellationToken);
        if ((await cursor.ToListAsync(cancellationToken)).Any(index => index["name"] == oldIndex))
            await collection.Indexes.DropOneAsync(oldIndex, cancellationToken);

        await collection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys
                    .Ascending("TenantId")
                    .Ascending("Bindings.Consumer.ResourceType")
                    .Ascending("Bindings.Consumer.ResourceId")),
            cancellationToken: cancellationToken);
    }
}
