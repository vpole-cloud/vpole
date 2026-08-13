using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;

namespace Pole.Features.Domains.Application.Migrations;

public sealed class TenantDomainZoneSchemaValidationMigration : IMigration
{
    private const string CollectionName = "TenantDomainZones";

    public ObjectId Id => new("6a900001b213917a38c38101");
    public string Name => "Tenant domain zone document schema validation";
    public string Description => "Creates or upgrades strict validation for tenant domain zones.";

    public Task Up(IMongoDatabase context, CancellationToken cancellationToken = default) =>
        DomainCollectionSchema.ApplyAsync(context, CollectionName, CreateValidator(), cancellationToken);

    private static BsonDocument CreateValidator() => new(
        "$jsonSchema",
        DomainCollectionSchema.Object(
            ["_id", "TenantId", "DeploymentTargetId", "Label", "Domain", "Kind", "Revision", "CreatedAtUtc", "UpdatedAtUtc"],
            ("_id", DomainCollectionSchema.Type("objectId")),
            ("TenantId", DomainCollectionSchema.Type("objectId")),
            ("DeploymentTargetId", DomainCollectionSchema.Type("objectId")),
            ("Label", DomainCollectionSchema.String(1, 63, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")),
            ("Domain", DomainCollectionSchema.String(1, 253, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$")),
            ("Kind", DomainCollectionSchema.Enum("Generated", "ManagedReservation")),
            ("Revision", DomainCollectionSchema.Integer("long", 1)),
            ("CreatedAtUtc", DomainCollectionSchema.Type("date")),
            ("UpdatedAtUtc", DomainCollectionSchema.Type("date"))));
}
