using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Tenants.Migrations;

public sealed class TenantInvitationIndexesMigration : IMigration
{
    public ObjectId Id => new("6b100001a213917a38c38001");
    public string Name => "Tenant invitation identity and token indexes";

    public string Description =>
        "Unique tenant recipient slots, opaque token lookup, and immutable acceptance receipts.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        await ApplySchemaAsync(context, "TenantInvitations", InvitationSchema(), cancellationToken);
        await ApplySchemaAsync(context, "TenantInvitationAcceptances", AcceptanceSchema(), cancellationToken);
        await context.GetCollection<BsonDocument>("TenantInvitations")
            .Indexes
            .CreateManyAsync(
                [
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Email"),
                        new CreateIndexOptions { Unique = true }),
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys.Ascending("TokenHash"),
                        new CreateIndexOptions { Unique = true }),
                ],
                cancellationToken);
        // Receipts are keyed by token hash through MongoDB's unique _id index and never expire.
    }

    private static async Task ApplySchemaAsync(
        IMongoDatabase database,
        string collectionName,
        BsonDocument schema,
        CancellationToken ct)
    {
        var validator = new BsonDocument("$jsonSchema", schema);
        using var names = await database.ListCollectionNamesAsync(
            new ListCollectionNamesOptions { Filter = new BsonDocument("name", collectionName) },
            ct);
        if (!await names.AnyAsync(ct))
        {
            await database.CreateCollectionAsync(
                collectionName,
                new CreateCollectionOptions<BsonDocument>
                {
                    Validator = new BsonDocumentFilterDefinition<BsonDocument>(validator),
                    ValidationLevel = DocumentValidationLevel.Strict,
                    ValidationAction = DocumentValidationAction.Error,
                },
                ct);
            return;
        }

        await database.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["collMod"] = collectionName, ["validator"] = validator,
                ["validationLevel"] = "strict", ["validationAction"] = "error",
            },
            cancellationToken: ct);
    }

    private static BsonDocument InvitationSchema() => Object(
        ("_id", Hash()),
        ("TenantId", Text()),
        ("Email", Text()),
        ("Role", Choice(0, 1, 2, 3)),
        ("IssuerId", Text()),
        ("TokenHash", Hash()),
        ("DeliveryToken", EncryptionSchema()),
        ("Revision", PositiveLong()),
        ("CreatedAtUtc", Type("date")),
        ("ExpiresAtUtc", Type("date")),
        ("Status", Choice(0, 1, 2)),
        ("DeliveryStatus", Choice(0, 1, 2)),
        ("OnboardingSubject", new BsonDocument("bsonType", new BsonArray { "string", "null" })));

    private static BsonDocument AcceptanceSchema() => Object(
        ("_id", Hash()),
        ("TenantId", Text()),
        ("Subject", Text()),
        ("Email", Text()),
        ("MemberId", Text()),
        ("DefaultTenantId", Text()),
        ("AcceptedAtUtc", Type("date")));

    private static BsonDocument EncryptionSchema() => Object(
        ("FormatVersion", Type("int")),
        ("KeyProviderId", Text()),
        ("KeyEncryptionKeyId", Text()),
        ("KeyEncryptionKeyVersion", Type("int")),
        ("KeyWrapAlgorithm", Text()),
        ("KeyWrapAlgorithmVersion", Type("int")),
        ("WrappedDataEncryptionKey", Type("binData")),
        ("WrappedDataEncryptionKeyNonce", Type("binData")),
        ("WrappedDataEncryptionKeyTag", Type("binData")),
        ("ContentEncryptionAlgorithm", Text()),
        ("ContentEncryptionAlgorithmVersion", Type("int")),
        ("Ciphertext", Type("binData")),
        ("Nonce", Type("binData")),
        ("AuthenticationTag", Type("binData")));

    private static BsonDocument Object(params (string Name, BsonDocument Schema)[] properties) => new()
    {
        ["bsonType"] = "object", ["additionalProperties"] = false,
        ["required"] = new BsonArray(properties.Select(property => property.Name)),
        ["properties"]
            = new BsonDocument(properties.Select(property => new BsonElement(property.Name, property.Schema))),
    };

    private static BsonDocument Type(string type) => new("bsonType", type);
    private static BsonDocument Text() => new() { ["bsonType"] = "string", ["minLength"] = 1 };
    private static BsonDocument Hash() => new() { ["bsonType"] = "string", ["pattern"] = "^[0-9A-F]{64}$" };
    private static BsonDocument PositiveLong() => new() { ["bsonType"] = "long", ["minimum"] = 1 };
    private static BsonDocument Choice(params int[] values) => new("enum", new BsonArray(values));
}
