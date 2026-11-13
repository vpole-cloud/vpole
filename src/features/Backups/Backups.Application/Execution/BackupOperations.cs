using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Backups.Events;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Execution;

public closed record BackupCommand(
    string TenantId,
    string Key
);

public sealed record ManualBackupCommand(
    string TenantId,
    string Key,
    string PolicyId
) : BackupCommand(TenantId, Key);

public sealed record RestoreArchiveCommand(
    string TenantId,
    string Key,
    string ArchiveId,
    BackupRestoreSelection Selection,
    string Name,
    int? StorageGiB
) : BackupCommand(TenantId, Key);

public sealed class BackupOperations(
    IMongoCollection<BackupArchive> archives,
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupOperation> operations,
    BackupArchiveAccess access,
    BackupProviderCatalog providers,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox,
    TimeProvider clock
)
{
    public async Task<BackupWriteResult<BackupOperation>> StartAsync(BackupCommand command, CancellationToken ct)
    {
        var key = BackupDispatchFilters.Idempotency(command.TenantId, BackupOperationKeys.Command(command.Key));
        var already = await operations.Find(key).SingleOrDefaultAsync(ct);
        if (already is not null)
            return Match(already, command);
        var archiveId = command switch
        {
            ManualBackupCommand => string.Empty,
            RestoreArchiveCommand restore => restore.ArchiveId,
        };
        if (command is ManualBackupCommand manual)
        {
            var policy = await policies.Find(BackupsFilters<BackupPolicy>.ById(command.TenantId, manual.PolicyId))
                .SingleOrDefaultAsync(ct);
            if (policy is null || policy.Deleting)
                return new BackupWriteResult<BackupOperation>.Rejected("not-found");
            archiveId = policy.ArchiveId;
        }

        var archive = await archives.Find(BackupsFilters<BackupArchive>.ById(command.TenantId, archiveId))
            .SingleOrDefaultAsync(ct);
        if (archive is null)
            return new BackupWriteResult<BackupOperation>.Rejected("not-found");
        if (command is RestoreArchiveCommand restoreCommand)
        {
            if (restoreCommand.StorageGiB is { } requestedStorage && requestedStorage < archive.StorageGiB)
                return new BackupWriteResult<BackupOperation>.Rejected("insufficient-restore-storage");
            var invalid = await VerifyTargetAsync(archive, restoreCommand.Selection, ct);
            if (invalid is not null)
                return new BackupWriteResult<BackupOperation>.Rejected(invalid);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var id = ObjectId.GenerateNewId().ToString();
        var core = EventCore.Create(command.TenantId);
        var origin = new BackupExecutionOrigin(core.TraceId, core.CorrelationId);
        var operation = command switch
        {
            RestoreArchiveCommand restore => BackupOperation.RequestRestore(
                id,
                command.TenantId,
                BackupOperationKeys.Command(command.Key),
                archive.PolicyId,
                archive.Id,
                restore.Selection,
                new BackupRestoreResource(ObjectId.GenerateNewId().ToString(), restore.Name, restore.StorageGiB),
                now,
                origin),
            ManualBackupCommand => BackupOperation.RequestCopy(
                id,
                command.TenantId,
                BackupOperationKeys.Command(command.Key),
                archive.PolicyId,
                archive.Id,
                now,
                origin),
        };
        try
        {
            return await transactions.InvokeOutcomeAsync<BackupWriteResult<BackupOperation>>(
                async (tx, token) =>
                {
                    var fenced = await archives.UpdateOneAsync(
                        tx.Session,
                        BackupsFilters<BackupArchive>.ById(command.TenantId, archive.Id),
                        Builders<BackupArchive>.Update.Inc(a => a.ReferenceFence, 1),
                        cancellationToken: token);
                    if (fenced.MatchedCount != 1)
                        return tx.Abort(new BackupWriteResult<BackupOperation>.Rejected("not-found"));
                    if (command is ManualBackupCommand)
                    {
                        var active = BackupDispatchFilters.ActivePolicyArchive(
                            command.TenantId,
                            archive.PolicyId,
                            archive.Id);
                        if (!await policies.Find(tx.Session, active).AnyAsync(token))
                            return tx.Abort(new BackupWriteResult<BackupOperation>.Rejected("policy-changed"));
                    }

                    await operations.InsertOneAsync(tx.Session, operation, cancellationToken: token);
                    await Changed(tx.Session, operation, token);
                    await outbox(tx.Session, [BackupWorkRequests.Operation(operation)], token);
                    return tx.Commit(new BackupWriteResult<BackupOperation>.Saved(operation));
                },
                ct);
        }
        catch (MongoWriteException e) when (MongoDuplicateKey.IsForIndex(
                                                e,
                                                BackupsIndexes.OperationKey,
                                                BackupsCollections.Operations))
        {
            var raced = await operations.Find(key).SingleAsync(ct);
            return Match(raced, command);
        }
    }

    internal async Task<string?> VerifyTargetAsync(
        BackupArchive archive,
        BackupRestoreSelection selection,
        CancellationToken ct)
    {
        var provider = providers.Find(archive.ResourceType);
        if (provider is null || selection is BackupTimeSelection && !provider.SupportsPointInTimeRecovery)
            return "unsupported-provider";
        if (selection is BackupTimeSelection time && time.TargetTimeUtc > clock.GetUtcNow().UtcDateTime)
            return "target-unavailable";
        var result = await access.ListAsync(archive, provider, ct);
        if (result is BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Failure failure)
            return failure.Code;
        var catalog = ((BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Success)result).Value;
        var available = selection switch
        {
            BackupCopySelection copy => catalog.Any(c => c.BackupId == copy.BackupId),
            BackupTimeSelection target => catalog.Any(c => c.CompletedAt.UtcDateTime <= target.TargetTimeUtc),
        };
        return available ? null : "target-unavailable";
    }

    private static BackupWriteResult<BackupOperation> Match(BackupOperation operation, BackupCommand command)
    {
        var same = (command, operation.Work) switch
        {
            (ManualBackupCommand manual, ManualBackupWork) => operation.PolicyId == manual.PolicyId,
            (RestoreArchiveCommand restore, RestoreBackupWork work) => operation.ArchiveId == restore.ArchiveId &&
                work.Selection == restore.Selection && work.Resource.Name == restore.Name &&
                work.Resource.StorageGiB == restore.StorageGiB,
            (ManualBackupCommand, RestoreBackupWork or ScheduledBackupWork) => false,
            (RestoreArchiveCommand, ManualBackupWork or ScheduledBackupWork) => false,
        };
        return same
            ? new BackupWriteResult<BackupOperation>.Saved(operation)
            : new BackupWriteResult<BackupOperation>.Rejected("idempotency-conflict");
    }

    internal async Task InterruptScheduledObservationsAsync(
        string tenantId,
        string archiveId,
        ScheduledObservationInterruption interruption,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var running = BackupDispatchFilters.RunningScheduled(tenantId, archiveId);
        var reason = interruption switch
        {
            ScheduledObservationInterruption.SourceRemoved => "source-removed-before-final-observation",
            ScheduledObservationInterruption.ArchiveRetired => "archive-retired-before-final-observation",
            _ => throw new ArgumentOutOfRangeException(nameof(interruption)),
        };
        foreach (var operation in await operations.Find(session, running).ToListAsync(ct))
        {
            var interrupted = operation.InterruptObservation(reason, clock.GetUtcNow().UtcDateTime);
            await operations.ReplaceOneAsync(
                session,
                BackupsFilters<BackupOperation>.ById(tenantId, operation.Id),
                interrupted,
                cancellationToken: ct);
            await Changed(session, interrupted, ct);
        }
    }

    internal Task Changed(
        IClientSessionHandle session,
        BackupOperation op,
        CancellationToken ct) => outbox(session, [BackupWorkRequests.OperationChanged(op)], ct);
}
