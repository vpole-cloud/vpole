using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Migrations;

namespace Pole.Features.Backups.Migrations;

public sealed class BackupsIndexesMigration : IMigration
{
    public ObjectId Id => new("6aee6a0031800609592fefb1");
    public string Name => "Tenant backup configuration, archives and operation history";

    public string Description =>
        "Scope resource identities and command idempotency to their tenant and index durable dispatch.";

    public async Task Up(IMongoDatabase context, CancellationToken cancellationToken = default)
    {
        var keys = Builders<BsonDocument>.IndexKeys;
        await IndexAsync(
            "BackupDestinations",
            "UX_BackupDestinations_Tenant_Name",
            true,
            keys.Ascending("TenantId").Ascending("Name"));
        await IndexAsync(
            "BackupPolicies",
            "UX_BackupPolicies_Tenant_Name",
            true,
            keys.Ascending("TenantId").Ascending("Name"));
        await IndexAsync(
            "BackupPolicies",
            "UX_BackupPolicies_Tenant_Source",
            true,
            keys.Ascending("TenantId").Ascending("ResourceType").Ascending("ResourceId"));
        await IndexAsync(
            "BackupPolicies",
            "IX_BackupPolicies_Dispatch",
            false,
            keys.Ascending("Dispatch.NextAttemptAtUtc").Ascending("Dispatch.Lease.ExpiresAtUtc"));
        await IndexAsync(
            "BackupArchives",
            "IX_BackupArchives_Tenant_Created",
            false,
            keys.Ascending("TenantId").Descending("CreatedAt"));
        await IndexAsync(
            "BackupOperations",
            "UX_BackupOperations_Tenant_Key",
            true,
            keys.Ascending("TenantId").Ascending("IdempotencyKey"));
        await IndexAsync(
            "BackupOperations",
            "IX_BackupOperations_Dispatch",
            false,
            keys.Ascending("Work.Execution.Dispatch.NextAttemptAtUtc")
                .Ascending("Work.Execution.Dispatch.Lease.ExpiresAtUtc"));
        await IndexAsync(
            "BackupOperations",
            "IX_BackupOperations_Tenant_Created",
            false,
            keys.Ascending("TenantId").Descending("CreatedAt").Descending("_id"));

        Task IndexAsync(
            string collection,
            string name,
            bool unique,
            IndexKeysDefinition<BsonDocument> fields) => context.GetCollection<BsonDocument>(collection)
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<BsonDocument>(fields, new CreateIndexOptions { Name = name, Unique = unique }),
                cancellationToken: cancellationToken);
    }
}
