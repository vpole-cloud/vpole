// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Operations;

public sealed class ScheduledBackupHistory(
    IMongoCollection<BackupOperation> operations,
    StoreOutboxWrites outbox,
    TimeProvider clock
)
{
    internal async Task<bool> RecordBatchAsync(
        BackupArchive archive,
        MonitoringBackupHistory history,
        BackupHistoryBatch batch,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (batch.ConsumedCount is < 0 or > 200 || batch.Changes.Count > batch.ConsumedCount ||
            batch.Activities.Count > batch.ConsumedCount)
            throw new InvalidOperationException("A provider history batch exceeded its declared bound.");
        var changes = batch.Changes.GroupBy(change => change.NativeUid).Select(group => group.Last()).ToArray();
        var keys = changes.Select(change => BackupOperationKeys.Scheduled(archive.Id, change.NativeUid)).ToArray();
        var existing = await operations.Find(session, BackupHistoryFilters.ByKeys(archive.TenantId, keys))
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(operation => operation.IdempotencyKey);
        foreach (var change in changes)
        {
            var key = BackupOperationKeys.Scheduled(archive.Id, change.NativeUid);
            byKey.TryGetValue(key, out var previous);
            if (previous is { Work: not ScheduledBackupWork })
                throw new InvalidOperationException("A scheduled identity belongs to another operation kind.");
            if (previous is { State: not BackupOperationState.Running })
                continue;
            ScheduledBackupExecution execution = change.State switch
            {
                NativeScheduledBackupRunning => new RunningScheduledBackup(),
                NativeScheduledBackupCompleted completed => new CompletedScheduledBackup(completed.BackupId),
                NativeScheduledBackupFailed failed => new FailedScheduledBackup(failed.Reason),
                NativeScheduledBackupRemoved => new InterruptedScheduledBackup(
                    "native-backup-removed-before-final-observation"),
            };
            var operation = BackupOperation.ObserveScheduled(
                previous?.Id ?? ObjectId.GenerateNewId().ToString(),
                archive.TenantId,
                key,
                archive.PolicyId,
                archive.Id,
                change.NativeUid,
                batch.ObservedPolicyRevision,
                history.Checkpoint.ScanId,
                execution,
                previous?.CreatedAt ?? change.CreatedAt.UtcDateTime,
                change.UpdatedAt.UtcDateTime,
                previous?.Origin ?? history.ExecutionOrigin);
            if (operation == previous)
                continue;
            await operations.ReplaceOneAsync(
                session,
                BackupDispatchFilters.Idempotency(archive.TenantId, key),
                operation,
                new ReplaceOptions { IsUpsert = true },
                ct);
            if (previous?.State != operation.State)
                await outbox(session, [BackupWorkRequests.OperationChanged(operation)], ct);
        }

        if (batch.Position is not FollowNativeBackupHistory ||
            history.Checkpoint.Position is FollowingBackupHistoryPosition)
            return true;
        return await FinalizeBackfillAsync(archive, history, session, ct);
    }

    internal Task<bool> FinalizeBackfillAsync(
        BackupArchive archive,
        MonitoringBackupHistory history,
        IClientSessionHandle session,
        CancellationToken ct) => InterruptAsync(
        BackupHistoryFilters.Unseen(archive.TenantId, archive.Id, history.Checkpoint.ScanId),
        "native-backup-not-found-in-snapshot",
        session,
        ct);

    internal Task<bool> InterruptRemovedSourceAsync(
        BackupArchive archive,
        IClientSessionHandle session,
        CancellationToken ct) => InterruptAsync(
        BackupDispatchFilters.RunningScheduled(archive.TenantId, archive.Id),
        "source-removed-before-final-observation",
        session,
        ct);

    private async Task<bool> InterruptAsync(
        FilterDefinition<BackupOperation> filter,
        string reason,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var running = await operations.Find(session, filter)
            .SortBy(operation => operation.Id)
            .Limit(200)
            .ToListAsync(ct);
        foreach (var operation in running)
        {
            var interrupted = operation.InterruptObservation(reason, clock.GetUtcNow().UtcDateTime);
            await operations.ReplaceOneAsync(
                session,
                BackupsFilters<BackupOperation>.ById(operation.TenantId, operation.Id),
                interrupted,
                cancellationToken: ct);
            await outbox(session, [BackupWorkRequests.OperationChanged(interrupted)], ct);
        }

        return !await operations.Find(session, filter).AnyAsync(ct);
    }
}
