using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Domain;

namespace Pole.Features.Backups.Application.Mongo;

internal static class BackupsFilters<T>
    where T : IBackupDocument
{
    internal static FilterDefinition<T> Tenant(string tenantId) => Builders<T>.Filter.ForTenant(tenantId);

    internal static FilterDefinition<T> ById(string tenantId, string id) =>
        Tenant(tenantId) & Builders<T>.Filter.Eq(x => x.Id, id);
}

internal static class BackupConfigurationFilters<T>
    where T : IBackupConfigurationDocument
{
    internal static FilterDefinition<T> ByName(string tenantId, string name) =>
        BackupsFilters<T>.Tenant(tenantId) & Builders<T>.Filter.Eq(x => x.Name, name);

    internal static FilterDefinition<T> AtRevision(
        string tenantId,
        string id,
        long revision) => BackupsFilters<T>.ById(tenantId, id) & Builders<T>.Filter.Eq(x => x.Revision, revision);
}

internal static class BackupDestinationFilters
{
    internal static FilterDefinition<BackupDestination> Scope(
        string tenant,
        string groupId,
        string scopeId) => BackupsFilters<BackupDestination>.Tenant(tenant) &
        Builders<BackupDestination>.Filter.Eq(d => d.ResourceGroupId, groupId) &
        Builders<BackupDestination>.Filter.Eq(d => d.IsolationScopeId, scopeId);
}

internal static class BackupsPolicyFilters
{
    internal static FilterDefinition<BackupPolicy> ForDestination(string tenantId, string id) =>
        BackupsFilters<BackupPolicy>.Tenant(tenantId) & Builders<BackupPolicy>.Filter.Eq(x => x.DestinationId, id);

    internal static FilterDefinition<BackupPolicy> ForSource(
        string tenantId,
        string resourceType,
        string resourceId) => BackupsFilters<BackupPolicy>.Tenant(tenantId) &
        Builders<BackupPolicy>.Filter.Eq(x => x.ResourceType, resourceType) &
        Builders<BackupPolicy>.Filter.Eq(x => x.ResourceId, resourceId);
}

internal static class BackupDispatchFilters
{
    internal static FilterDefinition<BackupPolicy> PoliciesDue(DateTime now) =>
        Builders<BackupPolicy>.Filter.Where(p =>
            p.NextAttemptAt <= now && (p.LeaseUntil == null || p.LeaseUntil <= now));

    internal static FilterDefinition<BackupPolicy> ClaimedPolicy(
        BackupPolicy policy,
        string leaseId,
        DateTime now) =>
        BackupConfigurationFilters<BackupPolicy>.AtRevision(policy.TenantId, policy.Id, policy.Revision) &
        Builders<BackupPolicy>.Filter.Eq(x => x.LeaseId, leaseId) &
        Builders<BackupPolicy>.Filter.Gt(x => x.LeaseUntil, now);

    internal static FilterDefinition<BackupPolicy> ActivePolicyArchive(
        string tenant,
        string policy,
        string archive) => BackupsFilters<BackupPolicy>.ById(tenant, policy) &
        Builders<BackupPolicy>.Filter.Eq(x => x.ArchiveId, archive) &
        Builders<BackupPolicy>.Filter.Eq(x => x.Deleting, false);

    internal static FilterDefinition<BackupPolicy> ActiveSource(
        string tenant,
        string type,
        string source) => BackupsPolicyFilters.ForSource(tenant, type, source) &
        Builders<BackupPolicy>.Filter.Eq(x => x.Deleting, false);

    internal static FilterDefinition<BackupOperation> OperationsDue(DateTime now) =>
        Builders<BackupOperation>.Filter.Where(o => o.Kind != BackupOperationKind.ScheduledBackup &&
            (o.State == BackupOperationState.Pending || o.State == BackupOperationState.Running) &&
            o.NextAttemptAt <= now && (o.LeaseUntil == null || o.LeaseUntil <= now));

    internal static FilterDefinition<BackupOperation> ClaimedOperation(
        BackupOperation operation,
        string leaseId,
        DateTime now) => BackupsFilters<BackupOperation>.ById(operation.TenantId, operation.Id) &
        Builders<BackupOperation>.Filter.Eq(x => x.LeaseId, leaseId) &
        Builders<BackupOperation>.Filter.Gt(x => x.LeaseUntil, now);

    internal static FilterDefinition<BackupOperation> Idempotency(string tenant, string key) =>
        BackupsFilters<BackupOperation>.Tenant(tenant) &
        Builders<BackupOperation>.Filter.Eq(x => x.IdempotencyKey, key);

    internal static FilterDefinition<BackupOperation> RestoreTarget(
        string tenantId,
        string operationId,
        string archiveId,
        string newResourceId) => BackupsFilters<BackupOperation>.ById(tenantId, operationId) &
        Builders<BackupOperation>.Filter.Where(operation =>
            operation.Kind == BackupOperationKind.Restore && operation.ArchiveId == archiveId &&
            operation.NewResourceId == newResourceId);

    internal static FilterDefinition<BackupOperation> CanCreateRestoreTarget() =>
        Builders<BackupOperation>.Filter.Where(operation => !operation.RestoreTargetCreated &&
            (operation.State == BackupOperationState.Pending || operation.State == BackupOperationState.Running));

    internal static FilterDefinition<BackupOperation> RunningScheduled(string tenantId, string archiveId) =>
        BackupsFilters<BackupOperation>.Tenant(tenantId) & Builders<BackupOperation>.Filter.Where(operation =>
            operation.ArchiveId == archiveId && operation.Kind == BackupOperationKind.ScheduledBackup &&
            operation.State == BackupOperationState.Running);

    internal static FilterDefinition<BackupOperation> UnfinishedCommands(string tenant, string archiveId) =>
        BackupsFilters<BackupOperation>.Tenant(tenant) & Builders<BackupOperation>.Filter.Where(op =>
            op.ArchiveId == archiveId && op.Kind != BackupOperationKind.ScheduledBackup &&
            (op.State == BackupOperationState.Pending || op.State == BackupOperationState.Running));

    internal static FilterDefinition<BackupArchive> CredentialPlacement(
        string tenant,
        string destinationId,
        string placementId) => DestinationArchives(tenant, destinationId) &
        Builders<BackupArchive>.Filter.Eq(a => a.Placement.CredentialDeploymentId, placementId);

    internal static FilterDefinition<BackupArchive> DestinationArchives(string tenant, string id) =>
        BackupsFilters<BackupArchive>.Tenant(tenant) & Builders<BackupArchive>.Filter.Eq(x => x.DestinationId, id);
}
