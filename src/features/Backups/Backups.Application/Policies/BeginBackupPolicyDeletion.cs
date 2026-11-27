// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Policies;

public sealed record DeleteBackupPolicyCommand(
    string TenantId,
    string Id,
    long ExpectedRevision
);

public sealed record BackupPolicyDeletionRequested;

public union DeleteAcceptedBackupPolicyOutcome(
    BackupPolicyDeletionRequested,
    BackupPolicyMissing,
    BackupPolicyRevisionConflict);

public union DeleteManualBackupPolicyOutcome(
    BackupPolicyDeletionRequested,
    BackupPolicyMissing,
    BackupPolicyRevisionConflict,
    BackupPolicyGitOwned);

public sealed class BeginBackupPolicyDeletion(
    IMongoCollection<BackupPolicy> policies,
    MongoRunTransaction transactions,
    IGitOpsMutations git,
    StoreOutboxWrites outbox,
    TimeProvider clock
)
{
    public Task<DeleteManualBackupPolicyOutcome>
        ExecuteAsync(DeleteBackupPolicyCommand command, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<DeleteManualBackupPolicyOutcome>(
            async (transaction, token) =>
            {
                if (!await git.AuthorizeAsync(
                        new(command.TenantId, BackupDeclarationKinds.Policy, command.Id),
                        transaction.Session,
                        token))
                    return transaction.Abort(new DeleteManualBackupPolicyOutcome(new BackupPolicyGitOwned()));

                var accepted = await ExecuteAcceptedAsync(command, transaction.Session, token);
                DeleteManualBackupPolicyOutcome result = accepted switch
                {
                    BackupPolicyDeletionRequested success => success,
                    BackupPolicyMissing error => error,
                    BackupPolicyRevisionConflict error => error,
                };
                return result is BackupPolicyDeletionRequested ? transaction.Commit(result) : transaction.Abort(result);
            },
            ct);

    public async Task<DeleteAcceptedBackupPolicyOutcome> ExecuteAcceptedAsync(
        DeleteBackupPolicyCommand command,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var filter = BackupConfigurationFilters<BackupPolicy>.AtRevision(
            command.TenantId,
            command.Id,
            command.ExpectedRevision);
        var current = await policies.Find(session, BackupsFilters<BackupPolicy>.ById(command.TenantId, command.Id))
            .SingleOrDefaultAsync(ct);
        if (current is null)
            return new BackupPolicyMissing();
        if (current.Revision != command.ExpectedRevision)
            return new BackupPolicyRevisionConflict();
        if (current.Deleting)
            return new BackupPolicyDeletionRequested();

        var eventCore = EventCore.Create(command.TenantId);
        var retiring = current.BeginStopping(
            new BackupExecutionOrigin(eventCore.TraceId, eventCore.CorrelationId),
            clock.GetUtcNow().UtcDateTime);
        var saved = await policies.ReplaceOneAsync(session, filter, retiring, cancellationToken: ct);
        if (saved.MatchedCount != 1)
            return new BackupPolicyRevisionConflict();

        await outbox(session, BackupPolicyNotifications.Changed(retiring), ct);
        return new BackupPolicyDeletionRequested();
    }
}
