// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.RedisServices.Migrations;

public sealed class RedisSchemaValidationMigration : IMigration
{
    private const string CollectionName = "RedisServices";

    public ObjectId Id => new("6a700001b213917a38c38001");

    public string Name => "Redis document schema validation";

    public string Description => "Applies strict MongoDB schema validation to tenant-owned Redis service documents.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var validator = RedisDocumentSchema.CreateValidator();
        using var collectionNames = await context.ListCollectionNamesAsync(
            new ListCollectionNamesOptions
            {
                Filter = new BsonDocument("name", CollectionName),
            },
            cancellationToken);
        var collectionExists = await collectionNames.AnyAsync(cancellationToken);

        if (!collectionExists)
        {
            await context.CreateCollectionAsync(
                CollectionName,
                new CreateCollectionOptions<BsonDocument>
                {
                    Validator = new BsonDocumentFilterDefinition<BsonDocument>(validator),
                    ValidationLevel = DocumentValidationLevel.Strict,
                    ValidationAction = DocumentValidationAction.Error,
                },
                cancellationToken);
            return;
        }

        await context.RunCommandAsync<BsonDocument>(
            new BsonDocument
            {
                ["collMod"] = CollectionName,
                ["validator"] = validator,
                ["validationLevel"] = "strict",
                ["validationAction"] = "error",
            },
            cancellationToken: cancellationToken);
    }

    private static class RedisDocumentSchema
    {
        public static BsonDocument CreateValidator() => new(
            "$jsonSchema",
            Object(
                [
                    "_id",
                    "TenantId",
                    "Name",
                    "ResourceGroupId",
                    "Spec",
                    "Tags",
                    "Events",
                    "Revision",
                    "PlanningFence",
                    "Lifecycle",
                ],
                ("_id", Type("objectId")),
                ("TenantId", StringType(minimumLength: 1)),
                ("Name", StringType(minimumLength: 1, maximumLength: 30, pattern: "^[a-z0-9-]+$")),
                ("ResourceGroupId", StringType(minimumLength: 1)),
                ("Spec", RedisSpecSchema()),
                ("Tags", ArrayOf(StringType(), uniqueItems: true)),
                ("Events", ArrayOf(ServiceEventSchema())),
                ("Revision", IntegerType("long", minimum: 1)),
                ("PlanningFence", IntegerType("long", minimum: 0)),
                ("Lifecycle", LifecycleSchema())));

        private static BsonDocument RedisSpecSchema() => ClosedObject(
            ["Storage"],
            ("Storage",
                ClosedObject(
                    ["SizeGb", "StorageProfileId"],
                    ("SizeGb", IntegerType("int", minimum: 1, maximum: 1000)),
                    ("StorageProfileId", StringType(minimumLength: 1, maximumLength: 100)))));

        private static BsonDocument ServiceEventSchema() => Object(
            ["OccurredOn", "Message"],
            ("OccurredOn", Type("date")),
            ("Message", StringType()));

        private static BsonDocument LifecycleSchema() => OneOf(
            LifecycleVariant("PreparedRedisLifecycle"),
            LifecycleVariant("ActiveRedisLifecycle"),
            LifecycleVariant("DeletingRedisLifecycle", "RequestedAtUtc"));

        private static BsonDocument LifecycleVariant(string discriminator, string? dateProperty = null) =>
            dateProperty is null
                ? ClosedObject(["_t"], ("_t", AllowedValue(new BsonArray { "RedisLifecycle", discriminator })))
                : ClosedObject(
                    ["_t", dateProperty],
                    ("_t", AllowedValue(new BsonArray { "RedisLifecycle", discriminator })),
                    (dateProperty, Type("date")));

        private static BsonDocument Object(BsonArray required, params (string Name, BsonDocument Schema)[] properties)
        {
            var propertySchemas = new BsonDocument();
            foreach (var (name, schema) in properties)
                propertySchemas[name] = schema;

            return new BsonDocument
            {
                ["bsonType"] = "object",
                ["required"] = required,
                ["properties"] = propertySchemas,
            };
        }

        private static BsonDocument ClosedObject(
            BsonArray required,
            params (string Name, BsonDocument Schema)[] properties)
        {
            var schema = Object(required, properties);
            schema["additionalProperties"] = false;
            return schema;
        }

        private static BsonDocument Type(string bsonType) => new("bsonType", bsonType);

        private static BsonDocument StringType(
            int? minimumLength = null,
            int? maximumLength = null,
            string? pattern = null)
        {
            var schema = Type("string");
            if (minimumLength is not null)
                schema["minLength"] = minimumLength.Value;
            if (maximumLength is not null)
                schema["maxLength"] = maximumLength.Value;
            if (pattern is not null)
                schema["pattern"] = pattern;

            return schema;
        }

        private static BsonDocument ArrayOf(BsonDocument itemSchema, bool uniqueItems = false)
        {
            var schema = new BsonDocument
            {
                ["bsonType"] = "array",
                ["items"] = itemSchema,
            };
            if (uniqueItems)
                schema["uniqueItems"] = true;

            return schema;
        }

        private static BsonDocument IntegerType(
            string bsonType,
            int minimum,
            int? maximum = null)
        {
            var schema = new BsonDocument
            {
                ["bsonType"] = bsonType,
                ["minimum"] = minimum,
            };
            if (maximum is not null)
                schema["maximum"] = maximum.Value;

            return schema;
        }

        private static BsonDocument AllowedValue(BsonValue value) => new("enum", new BsonArray { value });

        private static BsonDocument OneOf(params BsonDocument[] schemas) => new("oneOf", new BsonArray(schemas));
    }
}
