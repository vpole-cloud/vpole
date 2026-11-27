// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Archives;

public closed record RetireBackupArchiveRequest(
    BackupPolicy Policy
);

public sealed record RetireRemovedBackupSource(
    BackupPolicy Policy
) : RetireBackupArchiveRequest(Policy);

public sealed record RetireStoppedBackupPolicy(
    BackupPolicy Policy
) : RetireBackupArchiveRequest(Policy);

public sealed record RetireReplacedBackupArchive(
    BackupPolicy Policy,
    BackupArchive Replacement
) : RetireBackupArchiveRequest(Policy);

public sealed class RetireBackupArchive(
    IMongoCollection<BackupArchive> archives,
    StoreOutboxWrites outbox,
    TimeProvider clock
)
{
    public async Task ExecuteAsync(
        RetireBackupArchiveRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var policy = request.Policy;
        var filter = BackupsFilters<BackupArchive>.ById(policy.TenantId, policy.ArchiveId);
        var current = await archives.Find(session, filter).SingleAsync(ct);
        var retired = request switch
        {
            RetireRemovedBackupSource or RetireStoppedBackupPolicy => current.Retire(),
            RetireReplacedBackupArchive replacement => current.RetireForReplacement(replacement.Replacement),
        };
        if (request is not RetireRemovedBackupSource)
            retired = retired with
            {
                History = retired.History.PrepareDrain(
                    policy.Revision + (request is RetireReplacedBackupArchive ? 1 : 0),
                    new(ObjectId.GenerateNewId().ToString(), policy.Dispatch.Origin),
                    clock.GetUtcNow().UtcDateTime)
            };
        await archives.ReplaceOneAsync(session, filter, retired, cancellationToken: ct);
        if (retired.History is MonitoringBackupHistory)
            await outbox(session, [BackupWorkRequests.History(retired)], ct);
    }
}
