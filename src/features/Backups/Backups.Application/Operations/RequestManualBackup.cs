// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Policies;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Domain;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Operations;

public sealed record ManualBackupCommand(
    string TenantId,
    string Key,
    string PolicyId
);

public sealed record BackupPolicyChangedDuringBackupRequest;

public union RequestManualBackupOutcome(
    BackupOperation,
    BackupPolicyMissing,
    BackupPolicyStopping,
    BackupArchiveMissing,
    BackupPolicyChangedDuringBackupRequest,
    BackupCommandIdempotencyConflict);

public sealed class RequestManualBackup(
    IMongoCollection<BackupArchive> archives,
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupOperation> operations,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox,
    TimeProvider clock
)
{
    public async Task<RequestManualBackupOutcome> ExecuteAsync(ManualBackupCommand command, CancellationToken ct)
    {
        var key = BackupDispatchFilters.Idempotency(command.TenantId, BackupOperationKeys.Command(command.Key));
        var previous = await operations.Find(key).SingleOrDefaultAsync(ct);
        if (previous is not null)
            return Match(previous);

        var policy = await policies.Find(BackupsFilters<BackupPolicy>.ById(command.TenantId, command.PolicyId))
            .SingleOrDefaultAsync(ct);
        if (policy is null)
            return new BackupPolicyMissing();
        if (policy.Deleting)
            return new BackupPolicyStopping();
        var archive = await archives.Find(BackupsFilters<BackupArchive>.ById(command.TenantId, policy.ArchiveId))
            .SingleOrDefaultAsync(ct);
        if (archive is null)
            return new BackupArchiveMissing();

        var core = EventCore.Create(command.TenantId);
        var operation = BackupOperation.RequestCopy(
            ObjectId.GenerateNewId().ToString(),
            command.TenantId,
            BackupOperationKeys.Command(command.Key),
            policy.Id,
            archive.Id,
            clock.GetUtcNow().UtcDateTime,
            new(core.TraceId, core.CorrelationId));
        try
        {
            return await transactions.InvokeOutcomeAsync<RequestManualBackupOutcome>(
                async (transaction, token) =>
                {
                    var fenced = await archives.UpdateOneAsync(
                        transaction.Session,
                        BackupsFilters<BackupArchive>.ById(command.TenantId, archive.Id),
                        Builders<BackupArchive>.Update.Inc(a => a.ReferenceFence, 1),
                        cancellationToken: token);
                    if (fenced.MatchedCount != 1)
                        return transaction.Abort(new RequestManualBackupOutcome(new BackupArchiveMissing()));
                    if (!await policies.Find(
                                transaction.Session,
                                BackupDispatchFilters.ActivePolicyArchive(command.TenantId, policy.Id, archive.Id))
                            .AnyAsync(token))
                        return transaction.Abort(
                            new RequestManualBackupOutcome(new BackupPolicyChangedDuringBackupRequest()));

                    await operations.InsertOneAsync(transaction.Session, operation, cancellationToken: token);
                    await outbox(
                        transaction.Session,
                        [BackupWorkRequests.OperationChanged(operation), BackupWorkRequests.Operation(operation)],
                        token);
                    return transaction.Commit(new RequestManualBackupOutcome(operation));
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

        RequestManualBackupOutcome Match(BackupOperation existing) =>
            BackupOperationIdempotency.Match(existing, command) switch
            {
                BackupOperation value => value,
                BackupCommandIdempotencyConflict error => error,
            };
    }
}
