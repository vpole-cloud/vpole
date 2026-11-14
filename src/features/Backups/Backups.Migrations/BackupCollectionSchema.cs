using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.Backups.Migrations;

// Frozen BSON definitions. Changes to Domain serializers require a new migration.
internal static class BackupCollectionSchema
{
    internal static async Task ApplyAsync(
        IMongoDatabase database,
        string collection,
        BsonDocument schema,
        CancellationToken ct)
    {
        var validator = new BsonDocument("$jsonSchema", schema);
        using var names = await database.ListCollectionNamesAsync(
            new ListCollectionNamesOptions { Filter = new BsonDocument("name", collection) },
            ct);
        if (!await names.AnyAsync(ct))
        {
            await database.CreateCollectionAsync(
                collection,
                new CreateCollectionOptions<BsonDocument>
                {
                    Validator = validator,
                    ValidationLevel = DocumentValidationLevel.Strict,
                    ValidationAction = DocumentValidationAction.Error,
                },
                ct);
            return;
        }

        await database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["collMod"] = collection,
                ["validator"] = validator,
                ["validationLevel"] = "strict",
                ["validationAction"] = "error",
            },
            cancellationToken: ct);
    }

    internal static BsonDocument Object(params (string Name, BsonDocument Schema)[] fields) => new()
    {
        ["bsonType"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new BsonArray(fields.Select(field => field.Name)),
        ["properties"] = new BsonDocument(fields.Select(field => new BsonElement(field.Name, field.Schema))),
    };

    internal static BsonDocument Variant(
        string root,
        string leaf,
        params (string Name, BsonDocument Schema)[] fields) => Object(
        [("_t", new BsonDocument("enum", new BsonArray { new BsonArray { root, leaf } })), .. fields]);

    internal static BsonDocument OneOf(params BsonDocument[] variants) => new("oneOf", new BsonArray(variants));

    internal static BsonDocument Type(string type) => new("bsonType", type);

    internal static BsonDocument Text() => new() { ["bsonType"] = "string", ["minLength"] = 1 };

    internal static BsonDocument Number(string type, long minimum) =>
        new() { ["bsonType"] = type, ["minimum"] = minimum };

    internal static BsonDocument Nullable(BsonDocument schema) => OneOf(Type("null"), schema);

    internal static BsonDocument Strings() =>
        new() { ["bsonType"] = "array", ["uniqueItems"] = true, ["items"] = Text() };

    internal static BsonDocument Origin() => Object(("TraceId", Text()), ("CorrelationId", Text()));

    internal static BsonDocument Dispatch() => OneOf(
        Variant("BackupDispatch", "AvailableBackupDispatch", ("NextAttemptAtUtc", Type("date")), ("Origin", Origin())),
        Variant(
            "BackupDispatch",
            "ClaimedBackupDispatch",
            ("NextAttemptAtUtc", Type("date")),
            ("Origin", Origin()),
            ("Lease", Object(("_id", Text()), ("ExpiresAtUtc", Type("date"))))));

    internal static BsonDocument Storage() => Object(
        ("Endpoint", Text()),
        ("Bucket", Text()),
        ("Prefix", Type("string")),
        ("CredentialSecretId", Text()),
        ("AccessKeyIdKey", Text()),
        ("SecretAccessKeyKey", Text()));

    internal static BsonDocument Suspension() => Object(
        ("PolicyId", Text()),
        ("PolicyRevision", Number("long", 1)),
        ("PublisherId", Text()));

    internal static BsonDocument Stream() => Object(("PublisherId", Text()), ("Version", Text()));
}
