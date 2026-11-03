using Pole.Migrations;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Features.ResourceDeployments.Migrations;

/// <summary>
/// Historical schema for the multi-target resource-deployment snapshot.
/// Keep this BSON schema self-contained; it deliberately does not derive from
/// the current domain records at migration time.
/// </summary>
public sealed class ResourceDeploymentSchemaValidationMigration : IMigration
{
    private const string CollectionName = "ResourceDeployments";

    public ObjectId Id => new("69d90002b213917a38c379f8");

    public string Name => "Resource deployment multi-target document schema validation";

    public string Description =>
        "Applies strict validation for logical resource revisions, fixed replicas, and resolved ingress hostname snapshots.";

    public async Task Up(
        IMongoDatabase context,
        CancellationToken cancellationToken = default)
    {
        var validator = CreateValidator();
        using var names = await context.ListCollectionNamesAsync(
            new ListCollectionNamesOptions
            {
                Filter = new BsonDocument("name", CollectionName)
            },
            cancellationToken);
        if (!await names.AnyAsync(cancellationToken))
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

    private static BsonDocument CreateValidator() => new(
        "$jsonSchema",
        new BsonDocument
        {
            ["bsonType"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new BsonArray
            {
                "_id", "TenantId", "ResourceType", "ResourceId", "ResourceGroupId",
                "IsolationScopeId", "RuntimeCellId", "PlacementKey", "PlacementCapability",
                "ResolvedPlacement", "ResourceRevision", "FixedReplicas", "Revision",
                "DesiredGeneration", "DesiredOperation", "ObservedGeneration", "State",
                "Endpoints", "CreatedAtUtc", "UpdatedAtUtc"
            },
            ["properties"] = new BsonDocument
            {
                ["_id"] = Type("objectId"),
                ["TenantId"] = Type("objectId"),
                ["ResourceType"] = String(1, 128),
                ["ReferenceFence"] = new BsonDocument
                {
                    ["bsonType"] = "long",
                    ["minimum"] = 0
                },
                ["ResourceId"] = Type("objectId"),
                ["ResourceGroupId"] = Type("objectId"),
                ["IsolationScopeId"] = Type("objectId"),
                ["RuntimeCellId"] = Type("objectId"),
                ["PlacementKey"] = String(1, 512),
                ["PlacementCapability"] = Integer("int", 0, 1),
                ["ResolvedPlacement"] = Type("object"),
                ["ResourceRevision"] = Integer("long", 1),
                ["FixedReplicas"] = Integer("int", 1),
                ["Revision"] = Integer("long", 1),
                ["DesiredGeneration"] = Integer("long", 1),
                ["DesiredOperation"] = Integer("int", 0, 1),
                ["ObservedGeneration"] = Integer("long", 0),
                ["State"] = Type("object"),
                ["Endpoints"] = Type("array"),
                ["CreatedAtUtc"] = Type("date"),
                ["UpdatedAtUtc"] = Type("date")
            }
        });

    private static BsonDocument Type(string type) => new("bsonType", type);

    private static BsonDocument String(int minimumLength, int maximumLength) => new()
    {
        ["bsonType"] = "string",
        ["minLength"] = minimumLength,
        ["maxLength"] = maximumLength
    };

    private static BsonDocument Integer(
        string type,
        long minimum,
        long? maximum = null)
    {
        var schema = new BsonDocument
        {
            ["bsonType"] = type,
            ["minimum"] = minimum
        };
        if (maximum is not null)
            schema["maximum"] = maximum.Value;
        return schema;
    }
}
