// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Operations;

internal sealed class RestoreTargetCreations(
    IMongoCollection<BackupOperation> operations,
    StoreOutboxWrites outbox,
    TimeProvider clock
)
{
    public async Task<RestoreTargetCreationDecision> RecordAsync(
        RecordRestoreTargetCreationRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("A restore receipt requires the target creation transaction.");

        var authorized = BackupDispatchFilters.RestoreTarget(
            request.TenantId,
            request.OperationId,
            request.ArchiveId,
            request.NewResourceId);
        var current = await operations.Find(session, authorized).SingleOrDefaultAsync(ct);
        if (current?.Work is not RestoreBackupWork restore)
            return RestoreTargetCreationDecision.OperationUnavailable;

        switch (restore.Execution)
        {
            case ProvisioningBackupRestore:
            case CompletedBackupRestore:
            case FailedBackupRestore { Target: RecordedRestoreTarget }:
                return RestoreTargetCreationDecision.AlreadyRecorded;
            case FailedBackupRestore { Target: AwaitingRestoreTarget }:
                return RestoreTargetCreationDecision.OperationUnavailable;
            case RequestedBackupRestore:
                var recorded = current.RecordRestoreTarget(request.NewResourceId, clock.GetUtcNow().UtcDateTime);
                var result = await operations.UpdateOneAsync(
                    session,
                    BackupDispatchFilters.OperationAtWork(current),
                    Builders<BackupOperation>.Update
                        .Set(o => o.Work, recorded.Work)
                        .Set(o => o.UpdatedAt, recorded.UpdatedAt),
                    cancellationToken: ct);
                if (result.MatchedCount != 1)
                    return RestoreTargetCreationDecision.OperationUnavailable;
                await outbox(session, [BackupWorkRequests.OperationChanged(recorded)], ct);
                return RestoreTargetCreationDecision.Recorded;
        }

        throw new InvalidOperationException("Unsupported restore execution.");
    }
}
