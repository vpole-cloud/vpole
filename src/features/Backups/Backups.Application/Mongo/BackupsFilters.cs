// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Linq.Expressions;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Operations;
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
        Builders<BackupPolicy>.Filter.Lte(p => p.Dispatch.NextAttemptAtUtc, now) &
        (Builders<BackupPolicy>.Filter.OfType<BackupDispatch, AvailableBackupDispatch>(p => p.Dispatch) |
            (Builders<BackupPolicy>.Filter.OfType<BackupDispatch, ClaimedBackupDispatch>(p => p.Dispatch) &
                Builders<BackupPolicy>.Filter.Lte(p => ((ClaimedBackupDispatch)p.Dispatch).Lease.ExpiresAtUtc, now)));

    internal static FilterDefinition<BackupPolicy> PolicyAtDispatch(BackupPolicy policy) =>
        BackupConfigurationFilters<BackupPolicy>.AtRevision(policy.TenantId, policy.Id, policy.Revision) &
        Builders<BackupPolicy>.Filter.Eq(p => p.Dispatch, policy.Dispatch);

    internal static FilterDefinition<BackupPolicy> ClaimedPolicy(
        BackupPolicy policy,
        string leaseId,
        DateTime now) =>
        BackupConfigurationFilters<BackupPolicy>.AtRevision(policy.TenantId, policy.Id, policy.Revision) &
        Builders<BackupPolicy>.Filter.OfType<BackupDispatch, ClaimedBackupDispatch>(p => p.Dispatch) &
        Builders<BackupPolicy>.Filter.Eq(p => ((ClaimedBackupDispatch)p.Dispatch).Lease.Id, leaseId) &
        Builders<BackupPolicy>.Filter.Gt(p => ((ClaimedBackupDispatch)p.Dispatch).Lease.ExpiresAtUtc, now);

    internal static FilterDefinition<BackupPolicy> ActivePolicyArchive(
        string tenant,
        string policy,
        string archive) => BackupsFilters<BackupPolicy>.ById(tenant, policy) &
        Builders<BackupPolicy>.Filter.Eq(x => x.ArchiveId, archive) &
        Builders<BackupPolicy>.Filter.OfType<BackupPolicyLifecycle, ActiveBackupPolicy>(x => x.Lifecycle);

    internal static FilterDefinition<BackupPolicy> ActiveSource(
        string tenant,
        string type,
        string source) => BackupsPolicyFilters.ForSource(tenant, type, source) &
        Builders<BackupPolicy>.Filter.OfType<BackupPolicyLifecycle, ActiveBackupPolicy>(x => x.Lifecycle);

    private static FilterDefinition<BackupOperation> Manual() =>
        Builders<BackupOperation>.Filter.OfType<BackupOperationWork, ManualBackupWork>(o => o.Work);

    private static FilterDefinition<BackupOperation> Restore() =>
        Builders<BackupOperation>.Filter.OfType<BackupOperationWork, RestoreBackupWork>(o => o.Work);

    private static FilterDefinition<BackupOperation> ActiveCommands() =>
        ActiveDispatches(_ => Builders<BackupOperation>.Filter.Empty);

    private static FilterDefinition<BackupOperation>
        ActiveDispatches(
            Func<Expression<Func<BackupOperation, BackupDispatch>>, FilterDefinition<BackupOperation>> predicate) =>
        (Manual() &
            Builders<BackupOperation>.Filter.OfType<BackupCopyExecution, RequestedBackupCopy>(o =>
                ((ManualBackupWork)o.Work).Execution) &
            predicate(o => ((RequestedBackupCopy)((ManualBackupWork)o.Work).Execution).Dispatch)) |
        (Manual() &
            Builders<BackupOperation>.Filter.OfType<BackupCopyExecution, RunningBackupCopy>(o =>
                ((ManualBackupWork)o.Work).Execution) &
            predicate(o => ((RunningBackupCopy)((ManualBackupWork)o.Work).Execution).Dispatch)) |
        (Restore() &
            Builders<BackupOperation>.Filter.OfType<BackupRestoreExecution, RequestedBackupRestore>(o =>
                ((RestoreBackupWork)o.Work).Execution) &
            predicate(o => ((RequestedBackupRestore)((RestoreBackupWork)o.Work).Execution).Dispatch)) | (Restore() &
            Builders<BackupOperation>.Filter.OfType<BackupRestoreExecution, ProvisioningBackupRestore>(o =>
                ((RestoreBackupWork)o.Work).Execution) & predicate(o =>
                ((ProvisioningBackupRestore)((RestoreBackupWork)o.Work).Execution).Dispatch));

    internal static FilterDefinition<BackupOperation> OperationsDue(DateTime now) =>
        ActiveDispatches(dispatch => Due(dispatch, now));

    private static FilterDefinition<BackupOperation> Due(
        Expression<Func<BackupOperation, BackupDispatch>> dispatch,
        DateTime now)
    {
        var next = Append(dispatch, d => d.NextAttemptAtUtc);
        var expires = Append(dispatch, d => ((ClaimedBackupDispatch)d).Lease.ExpiresAtUtc);
        return Builders<BackupOperation>.Filter.Lte(next, now) &
            (Builders<BackupOperation>.Filter.OfType<BackupDispatch, AvailableBackupDispatch>(dispatch) |
                (Builders<BackupOperation>.Filter.OfType<BackupDispatch, ClaimedBackupDispatch>(dispatch) &
                    Builders<BackupOperation>.Filter.Lte(expires, now)));
    }

    private static Expression<Func<T, TResult>> Append<T, TMember, TResult>(
        Expression<Func<T, TMember>> prefix,
        Expression<Func<TMember, TResult>> member)
    {
        var body = new Substitute(member.Parameters[0], prefix.Body).Visit(member.Body);
        return Expression.Lambda<Func<T, TResult>>(body, prefix.Parameters);
    }

    private sealed class Substitute(
        ParameterExpression parameter,
        Expression replacement
    ) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == parameter ? replacement : base.VisitParameter(node);
    }

    internal static FilterDefinition<BackupOperation> OperationAtWork(BackupOperation operation) =>
        BackupsFilters<BackupOperation>.ById(operation.TenantId, operation.Id) &
        Builders<BackupOperation>.Filter.Eq(o => o.Work, operation.Work);

    internal static FilterDefinition<BackupOperation> ClaimedOperation(
        BackupOperation operation,
        string leaseId,
        DateTime now) => BackupsFilters<BackupOperation>.ById(operation.TenantId, operation.Id) &
        ActiveDispatches(dispatch =>
            Builders<BackupOperation>.Filter.OfType<BackupDispatch, ClaimedBackupDispatch>(dispatch) &
            Builders<BackupOperation>.Filter.Eq(Append(dispatch, d => ((ClaimedBackupDispatch)d).Lease.Id), leaseId) &
            Builders<BackupOperation>.Filter.Gt(
                Append(dispatch, d => ((ClaimedBackupDispatch)d).Lease.ExpiresAtUtc),
                now));

    internal static FilterDefinition<BackupOperation> Idempotency(string tenant, string key) =>
        BackupsFilters<BackupOperation>.Tenant(tenant) &
        Builders<BackupOperation>.Filter.Eq(x => x.IdempotencyKey, key);

    internal static FilterDefinition<BackupOperation> RestoreTarget(
        string tenantId,
        string operationId,
        string archiveId,
        string newResourceId) => BackupsFilters<BackupOperation>.ById(tenantId, operationId) & Restore() &
        Builders<BackupOperation>.Filter.Eq(o => o.ArchiveId, archiveId) & Builders<BackupOperation>.Filter.Eq(
            o => ((RestoreBackupWork)o.Work).Resource.Id,
            newResourceId);

    internal static FilterDefinition<BackupOperation> RunningScheduled(string tenantId, string archiveId) =>
        BackupsFilters<BackupOperation>.Tenant(tenantId) &
        Builders<BackupOperation>.Filter.Eq(o => o.ArchiveId, archiveId) &
        Builders<BackupOperation>.Filter.OfType<BackupOperationWork, ScheduledBackupWork>(o => o.Work) &
        Builders<BackupOperation>.Filter.OfType<ScheduledBackupExecution, RunningScheduledBackup>(o =>
            ((ScheduledBackupWork)o.Work).Execution);

    internal static FilterDefinition<BackupOperation> UnfinishedCopies(string tenant, string archiveId) =>
        UnfinishedCommands(tenant, archiveId) & Manual();

    internal static FilterDefinition<BackupOperation> UnfinishedCommands(string tenant, string archiveId) =>
        BackupsFilters<BackupOperation>.Tenant(tenant) &
        Builders<BackupOperation>.Filter.Eq(o => o.ArchiveId, archiveId) & ActiveCommands();

    internal static FilterDefinition<BackupArchive> CredentialPlacement(
        string tenant,
        string destinationId,
        string placementId) => DestinationArchives(tenant, destinationId) &
        Builders<BackupArchive>.Filter.Eq(a => a.Placement.CredentialDeploymentId, placementId);

    internal static FilterDefinition<BackupArchive> DestinationArchives(string tenant, string id) =>
        BackupsFilters<BackupArchive>.Tenant(tenant) & Builders<BackupArchive>.Filter.Eq(x => x.DestinationId, id);
}

internal static class BackupHistoryFilters
{
    internal static FilterDefinition<BackupOperation> ByKeys(string tenantId, IReadOnlyList<string> keys) =>
        BackupsFilters<BackupOperation>.Tenant(tenantId) &
        Builders<BackupOperation>.Filter.In(o => o.IdempotencyKey, keys);

    internal static FilterDefinition<BackupArchive> Due(DateTime now) =>
        Builders<BackupArchive>.Filter.OfType<BackupArchiveHistory, MonitoringBackupHistory>(a => a.History) &
        Builders<BackupArchive>.Filter.Lte(a => ((MonitoringBackupHistory)a.History).Dispatch.NextAttemptAtUtc, now) &
        (Builders<BackupArchive>.Filter.OfType<BackupDispatch, AvailableBackupDispatch>(a =>
                ((MonitoringBackupHistory)a.History).Dispatch) |
            (Builders<BackupArchive>.Filter.OfType<BackupDispatch, ClaimedBackupDispatch>(a =>
                ((MonitoringBackupHistory)a.History).Dispatch) & Builders<BackupArchive>.Filter.Lte(
                a => ((ClaimedBackupDispatch)((MonitoringBackupHistory)a.History).Dispatch).Lease.ExpiresAtUtc,
                now)));

    internal static FilterDefinition<BackupArchive> AtRevision(BackupArchive archive) =>
        BackupsFilters<BackupArchive>.ById(archive.TenantId, archive.Id) &
        Builders<BackupArchive>.Filter.Eq(a => a.History.Revision, archive.History.Revision);

    internal static FilterDefinition<BackupArchive> Claimed(
        BackupArchive archive,
        string leaseId,
        DateTime now) => AtRevision(archive) &
        Builders<BackupArchive>.Filter.OfType<BackupArchiveHistory, MonitoringBackupHistory>(a => a.History) &
        Builders<BackupArchive>.Filter.OfType<BackupDispatch, ClaimedBackupDispatch>(a =>
            ((MonitoringBackupHistory)a.History).Dispatch) &
        Builders<BackupArchive>.Filter.Eq(
            a => ((ClaimedBackupDispatch)((MonitoringBackupHistory)a.History).Dispatch).Lease.Id,
            leaseId) & Builders<BackupArchive>.Filter.Gt(
            a => ((ClaimedBackupDispatch)((MonitoringBackupHistory)a.History).Dispatch).Lease.ExpiresAtUtc,
            now);

    internal static FilterDefinition<BackupOperation> Unseen(
        string tenantId,
        string archiveId,
        string scanId) => BackupDispatchFilters.RunningScheduled(tenantId, archiveId) &
        Builders<BackupOperation>.Filter.Ne(o => ((ScheduledBackupWork)o.Work).ScanId, scanId);

    internal static FilterDefinition<BackupOperation> Page(BackupOperationHistoryQuery query)
    {
        var filter = BackupsFilters<BackupOperation>.Tenant(query.TenantId);
        if (query.After is { } after)
            filter &= Builders<BackupOperation>.Filter.Lt(operation => operation.CreatedAt, after.CreatedAtUtc) |
                (Builders<BackupOperation>.Filter.Eq(operation => operation.CreatedAt, after.CreatedAtUtc) &
                    Builders<BackupOperation>.Filter.Lt(operation => operation.Id, after.Id));
        return filter;
    }
}
