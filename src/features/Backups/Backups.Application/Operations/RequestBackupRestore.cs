// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Operations;

public sealed record RestoreArchiveCommand(
    string TenantId,
    string Key,
    string ArchiveId,
    BackupRestoreSelection Selection,
    string Name,
    int? StorageGiB
);

public union RequestBackupRestoreOutcome(
    BackupOperation,
    BackupArchiveMissing,
    BackupCommandIdempotencyConflict,
    BackupRestoreStorageTooSmall,
    BackupRestoreCopyUnavailable,
    BackupRestoreTimeUnavailable,
    BackupRestoreTimeUnsupported,
    BackupSourceUnsupported,
    BackupCatalogCredentialUnavailable,
    BackupCatalogAccessDenied,
    BackupCatalogUnavailable,
    BackupCatalogUnreadable,
    BackupCatalogTimeout);

public sealed class RequestBackupRestore(
    IMongoCollection<BackupArchive> archives,
    IMongoCollection<BackupOperation> operations,
    VerifyBackupRestoreTarget verify,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox,
    TimeProvider clock
)
{
    public async Task<RequestBackupRestoreOutcome> ExecuteAsync(RestoreArchiveCommand command, CancellationToken ct)
    {
        var key = BackupDispatchFilters.Idempotency(command.TenantId, BackupOperationKeys.Command(command.Key));
        var previous = await operations.Find(key).SingleOrDefaultAsync(ct);
        if (previous is not null)
            return Match(previous);
        var archive = await archives.Find(BackupsFilters<BackupArchive>.ById(command.TenantId, command.ArchiveId))
            .SingleOrDefaultAsync(ct);
        if (archive is null)
            return new BackupArchiveMissing();
        if (command.StorageGiB is { } storage && storage < archive.StorageGiB)
            return new BackupRestoreStorageTooSmall();

        var target = await verify.ExecuteAsync(archive, command.Selection, ct);
        return target switch
        {
            BackupRestoreTargetAvailable => await PersistAsync(),
            BackupRestoreCopyUnavailable error => error,
            BackupRestoreTimeUnavailable error => error,
            BackupRestoreTimeUnsupported error => error,
            BackupSourceUnsupported error => error,
            BackupCatalogCredentialUnavailable error => error,
            BackupCatalogAccessDenied error => error,
            BackupCatalogUnavailable error => error,
            BackupCatalogUnreadable error => error,
            BackupCatalogTimeout error => error,
        };

        async Task<RequestBackupRestoreOutcome> PersistAsync()
        {
            var core = EventCore.Create(command.TenantId);
            var operation = BackupOperation.RequestRestore(
                ObjectId.GenerateNewId().ToString(),
                command.TenantId,
                BackupOperationKeys.Command(command.Key),
                archive.PolicyId,
                archive.Id,
                command.Selection,
                new BackupRestoreResource(ObjectId.GenerateNewId().ToString(), command.Name, command.StorageGiB),
                clock.GetUtcNow().UtcDateTime,
                new(core.TraceId, core.CorrelationId));
            try
            {
                return await transactions.InvokeOutcomeAsync<RequestBackupRestoreOutcome>(
                    async (transaction, token) =>
                    {
                        var fenced = await archives.UpdateOneAsync(
                            transaction.Session,
                            BackupsFilters<BackupArchive>.ById(command.TenantId, archive.Id),
                            Builders<BackupArchive>.Update.Inc(a => a.ReferenceFence, 1),
                            cancellationToken: token);
                        if (fenced.MatchedCount != 1)
                            return transaction.Abort(new RequestBackupRestoreOutcome(new BackupArchiveMissing()));

                        await operations.InsertOneAsync(transaction.Session, operation, cancellationToken: token);
                        await outbox(
                            transaction.Session,
                            [BackupWorkRequests.OperationChanged(operation), BackupWorkRequests.Operation(operation)],
                            token);
                        return transaction.Commit(new RequestBackupRestoreOutcome(operation));
                    },
                    ct);
            }
            catch (MongoWriteException error) when (MongoDuplicateKey.IsForIndex(
                                                        error,
                                                        BackupsIndexes.OperationKey,
                                                        BackupsCollections.Operations))
            {
                return Match(await operations.Find(key).SingleAsync(ct));
            }
        }

        RequestBackupRestoreOutcome Match(BackupOperation existing) =>
            BackupOperationIdempotency.Match(existing, command) switch
            {
                BackupOperation value => value,
                BackupCommandIdempotencyConflict error => error,
            };
    }
}
