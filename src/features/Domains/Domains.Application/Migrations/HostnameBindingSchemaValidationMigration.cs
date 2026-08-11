using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.Domains.Application.Migrations;

public sealed class HostnameBindingSchemaValidationMigration : IMigration
{
    private const string CollectionName = "HostnameBindings";

    public ObjectId Id => new("6a900002b213917a38c38102");
    public string Name => "Hostname binding document schema validation";
    public string Description => "Creates or upgrades strict validation for tenant hostname bindings.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) =>
        DomainCollectionSchema.ApplyAsync(context, CollectionName, CreateValidator(), cancellationToken);

    private static BsonDocument CreateValidator() => new(
        "$jsonSchema",
        DomainCollectionSchema.Object(
            ["_id", "TenantId", "TenantDomainZoneId", "DeploymentTargetId", "ResourceType", "ResourceId", "ResourceDeploymentId", "RelativeName", "Hostname", "Kind", "CreatedAtUtc"],
            ("_id", DomainCollectionSchema.Type("objectId")),
            ("TenantId", DomainCollectionSchema.Type("objectId")),
            ("TenantDomainZoneId", DomainCollectionSchema.Type("objectId")),
            ("DeploymentTargetId", DomainCollectionSchema.Type("objectId")),
            ("ResourceType", DomainCollectionSchema.String(1, 128, "^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?(?:/[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?)+$")),
            ("ResourceId", DomainCollectionSchema.Type("objectId")),
            ("ResourceDeploymentId", DomainCollectionSchema.Type("objectId")),
            ("RelativeName", DomainCollectionSchema.String(1, 253, "^(?:@|[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*)$")),
            ("Hostname", DomainCollectionSchema.String(1, 253, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$")),
            ("Kind", DomainCollectionSchema.Enum("Generated", "Custom")),
            ("CreatedAtUtc", DomainCollectionSchema.Type("date"))));
}
