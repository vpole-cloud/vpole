using Pole.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.WebServices.Migrations;

public sealed class PreparedWebServiceSchemaMigration : IMigration
{
    public ObjectId Id => new("6b700088b213917a38c38003");
    public string Name => "Prepared WebService lifecycle";
    public string Description => "Adds inert prepared identity before dependency configuration and activation.";
    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        using var collections = await context.ListCollectionsAsync(new ListCollectionsOptions
        { Filter = new BsonDocument("name", "WebServices") }, cancellationToken);
        var collection = await collections.FirstAsync(cancellationToken);
        var validator = collection["options"]["validator"].AsBsonDocument;
        var states = validator["$jsonSchema"]["properties"]["Lifecycle"]["oneOf"].AsBsonArray;
        var prepared = BsonDocument.Parse(states[0].ToJson().Replace("ActiveWebServiceLifecycle",
            "PreparedWebServiceLifecycle", StringComparison.Ordinal));
        if (!states.Any(state => state.ToJson().Contains("PreparedWebServiceLifecycle",
            StringComparison.Ordinal))) states.Add(prepared);
        await context.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            ["collMod"] = "WebServices",
            ["validator"] = validator,
            ["validationLevel"] = "strict",
            ["validationAction"] = "error"
        }, cancellationToken: cancellationToken);
    }
}
