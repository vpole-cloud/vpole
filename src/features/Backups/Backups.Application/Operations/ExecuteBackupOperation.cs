using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Operations;

public sealed class ExecuteBackupOperation(
    IMongoCollection<BackupArchive> archives,
    IMongoCollection<BackupOperation> operations,
    ExecuteBackupCopy copies,
    ExecuteBackupRestore restores,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox,
    TimeProvider clock,
    ILogger<ExecuteBackupOperation> logger
)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(3);

    public async Task<bool> ExecuteAsync(
        string tenantId,
        string operationId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var lockId = ObjectId.GenerateNewId().ToString();
        var candidate = await operations
            .Find(
                BackupDispatchFilters.OperationsDue(now) & BackupsFilters<BackupOperation>.ById(tenantId, operationId))
            .SortBy(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (candidate is null)
            return false;
        var operation = candidate.Claim(new BackupLease(lockId, now + Lease));
        var claimed = await operations.UpdateOneAsync(
            BackupDispatchFilters.OperationAtWork(candidate),
            Builders<BackupOperation>.Update.Set(o => o.Work, operation.Work),
            cancellationToken: ct);
        if (claimed.ModifiedCount != 1)
            return false;
        await BackupAttemptDiagnostics.RunAsync(
            logger,
            operation.Origin,
            tenantId,
            operationId,
            () => ExecuteClaimedAsync(operation, lockId, ct),
            ct);
        return true;
    }

    private async Task ExecuteClaimedAsync(
        BackupOperation operation,
        string lockId,
        CancellationToken ct)
    {
        var archive = await archives.Find(BackupsFilters<BackupArchive>.ById(operation.TenantId, operation.ArchiveId))
            .SingleAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Lease / 2);
        var apply = operation.Work switch
        {
            ManualBackupWork => await copies.ExecuteAsync(operation, archive, deadline.Token),
            RestoreBackupWork => await restores.ExecuteAsync(operation, archive, deadline.Token),
            ScheduledBackupWork => throw new InvalidOperationException("Scheduled backups are observed separately."),
        };
        var updatedAt = clock.GetUtcNow().UtcDateTime;
        await transactions.InvokeOutcomeAsync<bool>(
            async (tx, token) =>
            {
                var guard = BackupDispatchFilters.ClaimedOperation(operation, lockId, clock.GetUtcNow().UtcDateTime);
                var current = await operations.Find(tx.Session, guard).SingleOrDefaultAsync(token);
                if (current is null)
                    return tx.Abort(false);
                // The provider may have committed the creation receipt after this lease was claimed.
                var updated = apply(current, updatedAt);
                if ((await operations.ReplaceOneAsync(tx.Session, guard, updated, cancellationToken: token))
                    .MatchedCount != 1)
                    return tx.Abort(false);
                if (updated.State != current.State || updated.FailureCode != current.FailureCode)
                    await outbox(tx.Session, [BackupWorkRequests.OperationChanged(updated)], token);
                if (updated.Dispatch is AvailableBackupDispatch)
                    await outbox(tx.Session, [BackupWorkRequests.Operation(updated)], token);
                return tx.Commit(true);
            },
            ct);
    }
}
