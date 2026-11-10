using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.WebServices.Migrations;

public sealed class WebServiceDeclaredPlacementSchemaMigration : IMigration
{
    public ObjectId Id => new("6b730023b213917a38c38003");
    public string Name => "Resolved declarative placement identities";
    public string Description => "Retains name-to-identity bindings without exposing IDs as declaration inputs.";

    public async Task Up(IMongoDatabase database, CancellationToken cancellationToken = default)
    {
        using var descriptions = await database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = new BsonDocument("name", "WebServices") },
            cancellationToken);
        var description = await descriptions.FirstAsync(cancellationToken);
        var validator = description["options"]["validator"].AsBsonDocument.DeepClone().AsBsonDocument;
        var names = new[] { "DeploymentTargetName", "DeploymentTargetId", "ComputeProfileName", "ComputeProfileId" };
        validator["$jsonSchema"]["properties"]["DeclaredPlacement"] = new BsonDocument
        {
            ["bsonType"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new BsonArray(names),
            ["properties"] = new BsonDocument(
                names.Select(name => new BsonElement(
                    name,
                    new BsonDocument { ["bsonType"] = "string", ["minLength"] = 1 }))),
        };
        await database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["collMod"] = "WebServices",
                ["validator"] = validator,
                ["validationLevel"] = "strict",
                ["validationAction"] = "error",
            },
            cancellationToken: cancellationToken);
    }
}
