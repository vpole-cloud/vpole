using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.SeaweedFS.Migrations;

public sealed class SeaweedFSSchemaMigration : IMigration
{
    public ObjectId Id => new("6a700026b213917a38c38001");
    public string Name => "SeaweedFS tenant services and buckets";
    public string Description => "Creates tenant scoped SeaweedFS service and bucket indexes.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        foreach (var collection in new[] { "SeaweedFS", "SeaweedFSBuckets" })
        {
            var validator = CreateValidator(collection);
            var names = await context.ListCollectionNamesAsync(cancellationToken: cancellationToken);
            if (!(await names.ToListAsync(cancellationToken)).Contains(collection, StringComparer.Ordinal))
                await context.CreateCollectionAsync(
                    collection,
                    new CreateCollectionOptions<BsonDocument> { Validator = validator },
                    cancellationToken);
            else
                await context.RunCommandAsync<BsonDocument>(
                    new BsonDocument
                        { ["collMod"] = collection, ["validator"] = validator, ["validationLevel"] = "strict" },
                    cancellationToken: cancellationToken);
        }

        await context.GetCollection<BsonDocument>("SeaweedFS")
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("Name"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Unique = true, Name = "UX_SeaweedFS_Tenant_Name",
                        PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("ReservesName", true)
                    }),
                cancellationToken: cancellationToken);
        await context.GetCollection<BsonDocument>("SeaweedFSBuckets")
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("TenantId").Ascending("SeaweedFSId").Ascending("Name"),
                    new CreateIndexOptions<BsonDocument>
                    {
                        Unique = true, Name = "UX_SeaweedFSBuckets_Tenant_Service_Name",
                        PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("ReservesName", true)
                    }),
                cancellationToken: cancellationToken);
        foreach (var collection in new[] { "SeaweedFS", "SeaweedFSBuckets" })
            await context.GetCollection<BsonDocument>(collection)
                .Indexes
                .CreateOneAsync(
                    new CreateIndexModel<BsonDocument>(
                        Builders<BsonDocument>.IndexKeys
                            .Ascending("Lifecycle._t")
                            .Ascending("Lifecycle.DeletionDeadlineUtc")
                            .Ascending("_id"),
                        new CreateIndexOptions { Name = "IX_" + collection + "_DeletionDeadline" }),
                    cancellationToken: cancellationToken);
    }

    private static BsonDocument CreateValidator(string collection)
    {
        var lifecycle = new BsonDocument(
            "oneOf",
            new BsonArray
            {
                Variant("SeaweedFSLifecycle", "ActiveSeaweedFSLifecycle"),
                Variant(
                    "SeaweedFSLifecycle",
                    "DeletingSeaweedFSLifecycle",
                    ("RequestedAtUtc", Type("date")),
                    ("DeletionDeadlineUtc", Type("date"))),
                Variant(
                    "SeaweedFSLifecycle",
                    "PurgingSeaweedFSLifecycle",
                    ("RequestedAtUtc", Type("date")),
                    ("DeletionDeadlineUtc", Type("date"))),
                Variant("SeaweedFSLifecycle", "DeletedSeaweedFSLifecycle", ("DeletedAtUtc", Type("date")))
            });
        var common = new List<(string Name, BsonDocument Schema)>
        {
            ("_id", Type("objectId")), ("TenantId", Identity()),
            ("Name", new BsonDocument { ["bsonType"] = "string", ["minLength"] = 1 }),
            ("Revision", Positive("long")), ("Lifecycle", lifecycle), ("ReservesName", Type("bool"))
        };
        if (collection == "SeaweedFS")
        {
            var metadata = new BsonDocument(
                "oneOf",
                new BsonArray
                {
                    Variant("SeaweedFSMetadata", "EmbeddedSeaweedFSMetadata"),
                    Variant("SeaweedFSMetadata", "DedicatedPostgresSeaweedFSMetadata"),
                    Variant("SeaweedFSMetadata", "ExistingPostgresSeaweedFSMetadata", ("PostgresId", Identity()))
                });
            common.AddRange(
            [
                ("ResourceGroupId", Identity()), ("AdminSecretId", Identity()),
                ("Tags",
                    new BsonDocument { ["bsonType"] = "array", ["items"] = Type("string"), ["uniqueItems"] = true }),
                ("Spec",
                    Object(
                        ("Mode", new BsonDocument { ["bsonType"] = "int", ["enum"] = new BsonArray { 0, 1 } }),
                        ("Storage", Object(("SizeGb", Positive("int")), ("StorageProfileId", Identity()))),
                        ("Metadata", metadata))),
                ("QuotaCharge",
                    Object(
                        ("CpuMillis", Positive("long")),
                        ("MemoryMi", Positive("long")),
                        ("Instances", Positive("long")),
                        ("StorageGb", Positive("long"))))
            ]);
        }
        else
            common.AddRange([("SeaweedFSId", Identity()), ("SecretId", Identity())]);

        return new BsonDocument("$jsonSchema", Object(common.ToArray()));
    }

    private static BsonDocument Variant(
        string root,
        string discriminator,
        params (string Name, BsonDocument Schema)[] fields) => Object(
        new[] { ("_t", new BsonDocument("enum", new BsonArray { new BsonArray { root, discriminator } })) }
            .Concat(fields)
            .ToArray());

    private static BsonDocument Object(params (string Name, BsonDocument Schema)[] fields) => new()
    {
        ["bsonType"] = "object",
        ["required"] = new BsonArray(fields.Select(field => field.Name)),
        ["additionalProperties"] = false,
        ["properties"] = new BsonDocument(fields.Select(field => new BsonElement(field.Name, field.Schema)))
    };

    private static BsonDocument Type(string type) => new("bsonType", type);
    private static BsonDocument Positive(string type) => new() { ["bsonType"] = type, ["minimum"] = 1 };
    private static BsonDocument Identity() => new() { ["bsonType"] = "string", ["pattern"] = "^[a-f0-9]{24}$" };
}
