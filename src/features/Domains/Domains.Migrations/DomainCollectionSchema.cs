using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.Domains.Migrations;

internal static class DomainCollectionSchema
{
    public static async Task ApplyAsync(
        IMongoDatabase database,
        string collectionName,
        BsonDocument validator,
        CancellationToken cancellationToken)
    {
        using var names = await database.ListCollectionNamesAsync(
            new ListCollectionNamesOptions { Filter = new BsonDocument("name", collectionName) },
            cancellationToken);
        if (!await names.AnyAsync(cancellationToken))
        {
            await database.CreateCollectionAsync(
                collectionName,
                new CreateCollectionOptions<BsonDocument>
                {
                    Validator = new BsonDocumentFilterDefinition<BsonDocument>(validator),
                    ValidationLevel = DocumentValidationLevel.Strict,
                    ValidationAction = DocumentValidationAction.Error,
                },
                cancellationToken);
            return;
        }

        await database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["collMod"] = collectionName,
                ["validator"] = validator,
                ["validationLevel"] = "strict",
                ["validationAction"] = "error",
            },
            cancellationToken: cancellationToken);
    }

    public static BsonDocument Object(BsonArray required, params (string Name, BsonDocument Schema)[] properties)
    {
        var schemas = new BsonDocument();
        foreach (var (name, schema) in properties)
            schemas[name] = schema;
        return new BsonDocument
        {
            ["bsonType"] = "object",
            ["additionalProperties"] = false,
            ["required"] = required,
            ["properties"] = schemas,
        };
    }

    public static BsonDocument Identity() => new(
        "oneOf",
        new BsonArray
        {
            Object(["_t"], ("_t", Enum("GeneratedDomainIdentity"))),
            Object(
                ["_t", "TechnicalName"],
                ("_t", Enum("NamedDomainIdentity")),
                ("TechnicalName", String(1, 63, "^[a-z](?:[a-z0-9-]*[a-z0-9])?$"))),
        });

    public static BsonDocument Type(string bsonType) => new("bsonType", bsonType);

    public static BsonDocument String(
        int minimumLength,
        int maximumLength,
        string? pattern = null)
    {
        var schema = new BsonDocument
            { ["bsonType"] = "string", ["minLength"] = minimumLength, ["maxLength"] = maximumLength };
        if (pattern is not null)
            schema["pattern"] = pattern;
        return schema;
    }

    public static BsonDocument Integer(string bsonType, long minimum) =>
        new() { ["bsonType"] = bsonType, ["minimum"] = minimum };

    public static BsonDocument Enum(params string[] values) => new("enum", new BsonArray(values));
}
