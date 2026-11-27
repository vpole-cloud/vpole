// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Policies;
using Pole.Features.Backups.Domain;
using Pole.Features.ResourceGroups.Events;

namespace Pole.Features.Backups.Application.Archives;

public sealed class BackupSourceLifecycle(
    IMongoCollection<BackupPolicy> policies,
    MongoRunTransaction transactions,
    BeginBackupPolicyDeletion deletion
)
{
    public Task RetireAsync(ResourceUnregisteredEvent message, CancellationToken ct) =>
        transactions.InvokeOutcomeAsync<bool>(
            async (tx, token) =>
            {
                var tenant = message.EventCore.TenantId;
                var filter = BackupDispatchFilters.ActiveSource(tenant, message.ResourceType, message.ResourceId);
                var policy = await policies.Find(tx.Session, filter).SingleOrDefaultAsync(token);
                if (policy is null)
                    return tx.Abort(false);
                var result = await deletion.ExecuteAcceptedAsync(
                    new(tenant, policy.Id, policy.Revision),
                    tx.Session,
                    token);
                return result is BackupPolicyDeletionRequested ? tx.Commit(true) : tx.Abort(false);
            },
            ct);
}
