using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Backups.Events;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Execution;

public sealed record BackupCommand(
    string TenantId,
    string Key,
    string SourceId,
    bool Restore,
    string? Name,
    string? BackupId,
    DateTime? TargetTime,
    int? StorageGiB = null
);

public sealed class BackupOperations(
    IMongoCollection<BackupArchive> archives,
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupOperation> operations,
    BackupArchiveAccess access,
    BackupProviders providers,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox,
    TimeProvider clock
)
{
    public async Task<BackupWriteResult<BackupOperation>> StartAsync(BackupCommand command, CancellationToken ct)
    {
        var key = BackupDispatchFilters.Idempotency(command.TenantId, "command:" + command.Key);
        var already = await operations.Find(key).SingleOrDefaultAsync(ct);
        if (already is not null)
            return Match(already, command);
        var archiveId = command.SourceId;
        if (!command.Restore)
        {
            var policy = await policies.Find(BackupsFilters<BackupPolicy>.ById(command.TenantId, command.SourceId))
                .SingleOrDefaultAsync(ct);
            if (policy is null || policy.Deleting)
                return new BackupWriteResult<BackupOperation>.Rejected("not-found");
            archiveId = policy.ArchiveId;
        }

        var archive = await archives.Find(BackupsFilters<BackupArchive>.ById(command.TenantId, archiveId))
            .SingleOrDefaultAsync(ct);
        if (archive is null)
            return new BackupWriteResult<BackupOperation>.Rejected("not-found");
        if (command.Restore)
        {
            if (command.StorageGiB is { } requestedStorage && requestedStorage < archive.StorageGiB)
                return new BackupWriteResult<BackupOperation>.Rejected("insufficient-restore-storage");
            var invalid = await VerifyTargetAsync(archive, command.BackupId, command.TargetTime, ct);
            if (invalid is not null)
                return new BackupWriteResult<BackupOperation>.Rejected(invalid);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var id = ObjectId.GenerateNewId().ToString();
        var operation = command.Restore
            ? BackupOperation.RequestRestore(
                id,
                command.TenantId,
                "command:" + command.Key,
                archive.PolicyId,
                archive.Id,
                command.BackupId,
                command.TargetTime,
                command.Name!,
                ObjectId.GenerateNewId().ToString(),
                command.StorageGiB,
                now)
            : BackupOperation.RequestCopy(
                id,
                command.TenantId,
                "command:" + command.Key,
                archive.PolicyId,
                archive.Id,
                now);
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
                    if (!command.Restore)
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
                    return tx.Commit(new BackupWriteResult<BackupOperation>.Saved(operation));
                },
                ct);
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            var raced = await operations.Find(key).SingleAsync(ct);
            return Match(raced, command);
        }
    }

    internal async Task<string?> VerifyTargetAsync(
        BackupArchive archive,
        string? backupId,
        DateTime? targetTime,
        CancellationToken ct)
    {
        var provider = providers.Find(archive.ResourceType);
        if (provider is null || targetTime.HasValue && !provider.SupportsPointInTimeRecovery)
            return "unsupported-provider";
        if (targetTime > clock.GetUtcNow().UtcDateTime)
            return "target-unavailable";
        var result = await access.ListAsync(archive, provider, ct);
        if (result is BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Failure failure)
            return failure.Code;
        var catalog = ((BackupProviderResult<IReadOnlyList<BackupCatalogEntry>>.Success)result).Value;
        return (backupId is not null
            ? catalog.Any(c => c.BackupId == backupId)
            : catalog.Any(c => c.CompletedAt.UtcDateTime <= targetTime))
            ? null
            : "target-unavailable";
    }

    private static BackupWriteResult<BackupOperation> Match(BackupOperation op, BackupCommand cmd) =>
        (cmd.Restore
            ? op.Kind == BackupOperationKind.Restore && op.ArchiveId == cmd.SourceId
            : op.Kind == BackupOperationKind.Backup && op.PolicyId == cmd.SourceId) && op.Name == cmd.Name &&
        op.RestoreStorageGiB == cmd.StorageGiB &&
        (cmd.Restore ? op.BackupId == cmd.BackupId && op.TargetTime == cmd.TargetTime : true)
            ? new BackupWriteResult<BackupOperation>.Saved(op)
            : new BackupWriteResult<BackupOperation>.Rejected("idempotency-conflict");

    internal async Task InterruptScheduledObservationsAsync(
        string tenantId,
        string archiveId,
        bool sourceRemoved,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var running = BackupDispatchFilters.RunningScheduled(tenantId, archiveId);
        var reason = sourceRemoved
            ? "source-removed-before-final-observation"
            : "archive-retired-before-final-observation";
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
        CancellationToken ct) => outbox(
        session,
        [
            OutboxWrite.Create(
                new BackupOperationChangedEvent(op.Id, op.State.ToString(), EventCore.Create(op.TenantId)))
        ],
        ct);
}
