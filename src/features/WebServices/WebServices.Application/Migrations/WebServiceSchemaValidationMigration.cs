using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.WebServices.Application.Migrations;

public sealed class WebServiceSchemaValidationMigration : IMigration
{
    private const string CollectionName = "WebServices";

    public ObjectId Id => new("6a700002b213917a38c38002");

    public string Name => "Web service document schema validation";

    public string Description =>
        "Applies strict MongoDB schema validation to tenant-owned web service documents.";

    public async Task Up(
        IMongoDatabase context,
        CancellationToken cancellationToken = default)
    {
        var validator = WebServiceDocumentSchema.CreateValidator();
        using var collectionNames = await context.ListCollectionNamesAsync(
            new ListCollectionNamesOptions
            {
                Filter = new BsonDocument("name", CollectionName)
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

    private static class WebServiceDocumentSchema
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
                    "DnsRecord",
                    "ActiveRevision",
                    "Tags",
                    "Events",
                    "Revision",
                    "Lifecycle"
                ],
                ("_id", Type("objectId")),
                ("TenantId", StringType(minimumLength: 1)),
                ("Name", StringType(minimumLength: 1, maximumLength: 30)),
                ("ResourceGroupId", StringType(minimumLength: 1)),
                ("Spec", WebServiceSpecSchema()),
                ("DnsRecord", DnsRecordSchema()),
                ("ActiveRevision", IntegerType("int", minimum: 1)),
                ("Tags", ArrayOf(StringType(), uniqueItems: true)),
                ("Events", ArrayOf(ServiceEventSchema())),
                ("Revision", IntegerType("long", minimum: 1)),
                ("Lifecycle", LifecycleSchema())));

        private static BsonDocument WebServiceSpecSchema() => Object(
            ["Image", "Replicas", "Config", "Autoscaling", "Probes"],
            ("Image", Object(
                ["Server", "Name", "Tag", "SecretName"],
                ("Server", StringType(minimumLength: 1, maximumLength: 255)),
                ("Name", StringType(minimumLength: 1, maximumLength: 255)),
                ("Tag", StringType(minimumLength: 1, maximumLength: 100)),
                ("SecretName", Nullable(StringType())))),
            ("Replicas", IntegerType("int", minimum: 1)),
            ("Config", ConfigSchema()),
            ("Autoscaling", Nullable(AutoscalingSchema())),
            ("Probes", ProbeSpecSchema()));

        private static BsonDocument ConfigSchema() => Object(
            ["EnvironmentVariables", "PortMappings"],
            ("EnvironmentVariables", Type("object")),
            ("PortMappings", ArrayOf(PortMappingSchema())));

        private static BsonDocument PortMappingSchema() => Object(
            ["ServicePort", "TargetPort", "Protocol", "Name"],
            ("ServicePort", IntegerType("int", minimum: 1, maximum: 65535)),
            ("TargetPort", StringType(minimumLength: 1, maximumLength: 10)),
            ("Protocol", AllowedValues("TCP", "UDP", "SCTP")),
            ("Name", StringType()));

        private static BsonDocument AutoscalingSchema() => Object(
            ["MinReplicas", "MaxReplicas", "Metric"],
            ("MinReplicas", Type("int")),
            ("MaxReplicas", Type("int")),
            ("Metric", Object(
                ["Type", "TargetAverageUtilization"],
                ("Type", StringType()),
                ("TargetAverageUtilization", Type("int")))));

        private static BsonDocument ProbeSpecSchema() => Object(
            ["Startup", "Liveness", "Readiness"],
            ("Startup", ProbeConfigSchema()),
            ("Liveness", ProbeConfigSchema()),
            ("Readiness", ProbeConfigSchema()));

        private static BsonDocument ProbeConfigSchema() => Object(
            [
                "Path",
                "Port",
                "InitialDelaySeconds",
                "PeriodSeconds",
                "TimeoutSeconds",
                "FailureThreshold",
                "SuccessThreshold"
            ],
            ("Path", StringType(minimumLength: 1, maximumLength: 200)),
            ("Port", StringType(minimumLength: 1, maximumLength: 10)),
            ("InitialDelaySeconds", IntegerType("int", minimum: 0, maximum: 300)),
            ("PeriodSeconds", IntegerType("int", minimum: 1, maximum: 120)),
            ("TimeoutSeconds", IntegerType("int", minimum: 1, maximum: 60)),
            ("FailureThreshold", IntegerType("int", minimum: 1, maximum: 10)),
            ("SuccessThreshold", IntegerType("int", minimum: 1, maximum: 10)));

        private static BsonDocument DnsRecordSchema() => Object(
            ["_id", "Domain"],
            ("_id", StringType(minimumLength: 1)),
            ("Domain", StringType(minimumLength: 1)));

        private static BsonDocument ServiceEventSchema() => Object(
            ["OccurredOn", "Message"],
            ("OccurredOn", Type("date")),
            ("Message", StringType()));

        private static BsonDocument LifecycleSchema() => OneOf(
            LifecycleVariant("ActiveWebServiceLifecycle"),
            LifecycleVariant("DeletingWebServiceLifecycle", "RequestedAtUtc"));

        private static BsonDocument LifecycleVariant(
            string discriminator,
            string? dateProperty = null) =>
            dateProperty is null
                ? ClosedObject(
                    ["_t"],
                    ("_t", AllowedValue(
                        new BsonArray { "WebServiceLifecycle", discriminator })))
                : ClosedObject(
                    ["_t", dateProperty],
                    ("_t", AllowedValue(
                        new BsonArray { "WebServiceLifecycle", discriminator })),
                    (dateProperty, Type("date")));

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
            int? minimumLength = null,
            int? maximumLength = null)
        {
            var schema = Type("string");
            if (minimumLength is not null)
                schema["minLength"] = minimumLength.Value;
            if (maximumLength is not null)
                schema["maxLength"] = maximumLength.Value;

            return schema;
        }

        private static BsonDocument ArrayOf(
            BsonDocument itemSchema,
            bool uniqueItems = false)
        {
            var schema = new BsonDocument
            {
                ["bsonType"] = "array",
                ["items"] = itemSchema
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
                ["minimum"] = minimum
            };
            if (maximum is not null)
                schema["maximum"] = maximum.Value;

            return schema;
        }

        private static BsonDocument Nullable(BsonDocument schema) => OneOf(
            Type("null"),
            schema);

        private static BsonDocument AllowedValue(BsonValue value) => new(
            "enum",
            new BsonArray { value });

        private static BsonDocument AllowedValues(params string[] values) => new(
            "enum",
            new BsonArray(values));

        private static BsonDocument OneOf(params BsonDocument[] schemas) => new(
            "oneOf",
            new BsonArray(schemas));
    }
}
