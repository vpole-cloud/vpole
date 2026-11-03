using Pole.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.ComputeProfiles.Migrations;

public sealed class ComputeProfileSchemaValidationMigration : IMigration
{
    private const string CollectionName = "ComputeProfiles";

    public ObjectId Id => new("6a700003b213917a38c38003");

    public string Name => "Compute profile document schema validation";

    public string Description =>
        "Applies strict MongoDB schema validation to portable compute profile documents.";

    public async Task Up(
        IMongoDatabase context,
        CancellationToken cancellationToken = default)
    {
        var validator = ComputeProfileDocumentSchema.CreateValidator();
        using var names = await context.ListCollectionNamesAsync(
            new ListCollectionNamesOptions
            {
                Filter = new BsonDocument("name", CollectionName)
            },
            cancellationToken);
        var exists = await names.AnyAsync(cancellationToken);
        if (!exists)
        {
            await context.CreateCollectionAsync(
                CollectionName,
                new CreateCollectionOptions<BsonDocument>
                {
                    Validator = new BsonDocumentFilterDefinition<BsonDocument>(validator),
                    ValidationLevel = DocumentValidationLevel.Strict,
                    ValidationAction = DocumentValidationAction.Error
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
                ["validationAction"] = "error"
            },
            cancellationToken: cancellationToken);
    }

    private static class ComputeProfileDocumentSchema
    {
        public static BsonDocument CreateValidator() => new(
            "$jsonSchema",
            Object(
                [
                    "_id",
                    "TechnicalName",
                    "DisplayName",
                    "Description",
                    "Resources",
                    "Architecture",
                    "ExtendedResources",
                    "AllowedResourceTypes",
                    "TenantAccess",
                    "TargetMappings",
                    "IsEnabled",
                    "IsDefault",
                    "Revision",
                    "ResolutionFence",
                    "CreatedAtUtc",
                    "UpdatedAtUtc"
                ],
                ("_id", Type("objectId")),
                ("TechnicalName", StringType(1, 63, "^[a-z](?:[a-z0-9-]*[a-z0-9])?$")),
                ("DisplayName", StringType(1, 80)),
                ("Description", StringType(0, 500)),
                ("Resources", ResourceRequirementsSchema()),
                ("Architecture", IntegerType("int", 0, 2)),
                ("ExtendedResources", ArrayOf(ExtendedResourceSchema())),
                ("AllowedResourceTypes", ArrayOf(
                    StringType(1, 128, "^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?(?:/[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?)+$"),
                    uniqueItems: true)),
                ("TenantAccess", TenantAccessSchema()),
                ("TargetMappings", ArrayOf(TargetMappingSchema())),
                ("IsEnabled", Type("bool")),
                ("IsDefault", Type("bool")),
                ("Revision", IntegerType("long", 1)),
                ("ResolutionFence", IntegerType("long", 0)),
                ("CreatedAtUtc", Type("date")),
                ("UpdatedAtUtc", Type("date"))));

        private static BsonDocument ResourceRequirementsSchema() => Object(
            ["Requests", "Limits"],
            ("Requests", ResourceQuantitySchema()),
            ("Limits", ResourceQuantitySchema()));

        private static BsonDocument ResourceQuantitySchema() => Object(
            ["CpuMillis", "MemoryMi"],
            ("CpuMillis", IntegerType("long", 1)),
            ("MemoryMi", IntegerType("long", 1)));

        private static BsonDocument ExtendedResourceSchema() => Object(
            ["Name", "Quantity"],
            ("Name", StringType(3, 317)),
            ("Quantity", IntegerType("long", 1)));

        private static BsonDocument TenantAccessSchema() => OneOf(
            ClosedObject(
                ["_t"],
                ("_t", AllowedValue(new BsonArray
                {
                    "ComputeProfileTenantAccess",
                    "all-tenants"
                }))),
            ClosedObject(
                ["_t", "TenantIds"],
                ("_t", AllowedValue(new BsonArray
                {
                    "ComputeProfileTenantAccess",
                    "selected-tenants"
                })),
                ("TenantIds", ArrayOf(StringType(24, 24, "^[0-9a-fA-F]{24}$"), uniqueItems: true))));

        private static BsonDocument TargetMappingSchema() => Object(
            [
                "DeploymentTargetId",
                "NodeSelector",
                "RequiredNodeAffinity",
                "PreferredNodeAffinity",
                "Tolerations",
                "RuntimeClassName"
            ],
            ("DeploymentTargetId", Type("objectId")),
            ("NodeSelector", ArrayOf(NodeSelectorSchema())),
            ("RequiredNodeAffinity", ArrayOf(NodeAffinityTermSchema())),
            ("PreferredNodeAffinity", ArrayOf(PreferredNodeAffinityTermSchema())),
            ("Tolerations", ArrayOf(TolerationSchema())),
            ("RuntimeClassName", Nullable(StringType(1, 253))));

        private static BsonDocument NodeSelectorSchema() => Object(
            ["Key", "Value"],
            ("Key", StringType(1, 317)),
            ("Value", StringType(0, 63)));

        private static BsonDocument NodeAffinityTermSchema() => Object(
            ["Expressions"],
            ("Expressions", ArrayOf(NodeAffinityExpressionSchema())));

        private static BsonDocument NodeAffinityExpressionSchema() => Object(
            ["Key", "Operator", "Values"],
            ("Key", StringType(1, 317)),
            ("Operator", IntegerType("int", 0, 5)),
            ("Values", ArrayOf(StringType(0, 63), uniqueItems: true)));

        private static BsonDocument PreferredNodeAffinityTermSchema() => Object(
            ["Weight", "Term"],
            ("Weight", IntegerType("int", 1, 100)),
            ("Term", NodeAffinityTermSchema()));

        private static BsonDocument TolerationSchema() => Object(
            ["Key", "Operator", "Value", "Effect", "TolerationSeconds"],
            ("Key", Nullable(StringType(1, 317))),
            ("Operator", IntegerType("int", 0, 1)),
            ("Value", Nullable(StringType(0, 63))),
            ("Effect", IntegerType("int", 0, 3)),
            ("TolerationSeconds", Nullable(IntegerType("long", 0))));

        private static BsonDocument Object(
            BsonArray required,
            params (string Name, BsonDocument Schema)[] properties)
        {
            var propertySchemas = new BsonDocument();
            foreach (var (name, schema) in properties)
                propertySchemas[name] = schema;
            return new BsonDocument
            {
                ["bsonType"] = "object",
                ["required"] = required,
                ["properties"] = propertySchemas
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
            int minimumLength,
            int maximumLength,
            string? pattern = null)
        {
            var schema = new BsonDocument
            {
                ["bsonType"] = "string",
                ["minLength"] = minimumLength,
                ["maxLength"] = maximumLength
            };
            if (pattern is not null)
                schema["pattern"] = pattern;
            return schema;
        }

        private static BsonDocument IntegerType(
            string bsonType,
            long minimum,
            long? maximum = null)
        {
            var schema = new BsonDocument
            {
                ["bsonType"] = bsonType,
                ["minimum"] = minimum
            };
            if (maximum is not null)
                schema["maximum"] = maximum.Value;
            return schema;
        }

        private static BsonDocument ArrayOf(
            BsonDocument items,
            bool uniqueItems = false)
        {
            var schema = new BsonDocument
            {
                ["bsonType"] = "array",
                ["items"] = items
            };
            if (uniqueItems)
                schema["uniqueItems"] = true;
            return schema;
        }

        private static BsonDocument Nullable(BsonDocument schema) =>
            OneOf(Type("null"), schema);

        private static BsonDocument AllowedValue(BsonValue value) => new(
            "enum",
            new BsonArray { value });

        private static BsonDocument OneOf(params BsonDocument[] schemas) => new(
            "oneOf",
            new BsonArray(schemas));
    }
}
