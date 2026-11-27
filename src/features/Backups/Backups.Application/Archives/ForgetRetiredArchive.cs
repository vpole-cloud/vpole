// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Common;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.Backups.Application.Archives;

public sealed record BackupArchiveForgotten;

public sealed record BackupArchiveMissing;

public sealed record BackupArchiveInUse;

public sealed record ArchivePlacementConflict;

public union ForgetRetiredArchiveOutcome(
    BackupArchiveForgotten,
    BackupArchiveMissing,
    BackupArchiveInUse,
    ArchivePlacementConflict);

public sealed class ForgetRetiredArchive(
    IMongoCollection<BackupArchive> archives,
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupDestination> destinations,
    IMongoCollection<BackupOperation> operations,
    MongoRunTransaction transactions,
    LoadResourceDeploymentPlanningSnapshot placements,
    RequestExactResourceDeploymentDeletion requestDeletion,
    StoreOutboxWrites outbox
)
{
    public async Task<ForgetRetiredArchiveOutcome> ExecuteAsync(
        string tenantId,
        string id,
        CancellationToken ct)
    {
        var filter = BackupsFilters<BackupArchive>.ById(tenantId, id);
        var selected = await archives.Find(filter).SingleOrDefaultAsync(ct);
        if (selected is null)
            return new BackupArchiveMissing();
        if (selected.Active)
            return new BackupArchiveInUse();
        var current = await policies.Find(BackupsFilters<BackupPolicy>.ById(tenantId, selected.PolicyId))
            .SingleOrDefaultAsync(ct);
        if (StillApplying(current, id))
            return new BackupArchiveInUse();

        return await transactions.InvokeOutcomeAsync<ForgetRetiredArchiveOutcome>(
            async (tx, token) =>
            {
                var archive = await archives.Find(tx.Session, filter).SingleOrDefaultAsync(token);
                if (archive is null)
                    return tx.Abort(new ForgetRetiredArchiveOutcome(new BackupArchiveMissing()));
                if (archive.Active || archive.History is MonitoringBackupHistory || await operations.Find(
                            tx.Session,
                            BackupDispatchFilters.UnfinishedCommands(tenantId, id))
                        .AnyAsync(token))
                    return tx.Abort(new ForgetRetiredArchiveOutcome(new BackupArchiveInUse()));
                var policy = await policies.Find(
                        tx.Session,
                        BackupsFilters<BackupPolicy>.ById(tenantId, archive.PolicyId))
                    .SingleOrDefaultAsync(token);
                if (StillApplying(policy, id))
                    return tx.Abort(new ForgetRetiredArchiveOutcome(new BackupArchiveInUse()));

                // Serialize detaching the last shared placement with another policy attaching a new archive.
                await destinations.UpdateOneAsync(
                    tx.Session,
                    BackupsFilters<BackupDestination>.ById(tenantId, archive.DestinationId),
                    Builders<BackupDestination>.Update.Inc(d => d.ReferenceFence, 1),
                    cancellationToken: token);
                await archives.DeleteOneAsync(tx.Session, filter, cancellationToken: token);
                var shared = BackupDispatchFilters.CredentialPlacement(
                    tenantId,
                    archive.DestinationId,
                    archive.Placement.CredentialDeploymentId);
                if (!await archives.Find(tx.Session, shared).AnyAsync(token))
                {
                    var type = BackupResourceTypes.Destination;
                    var result = await placements(new(tenantId, type, archive.DestinationId), tx.Session, token);
                    var item = result is ResourceDeploymentPlanningSnapshot remaining
                        ? remaining.Items.SingleOrDefault(d =>
                            d.DeploymentId == archive.Placement.CredentialDeploymentId)
                        : null;
                    if (item is { State: not ResourceDeploymentPlanningState.Deleting } && await requestDeletion(
                            new(
                                tenantId,
                                type,
                                archive.DestinationId,
                                item.DeploymentId,
                                item.Revision,
                                new(item.Compute.ComputeProfileId, item.Compute.ComputeProfileRevision)),
                            tx.Session,
                            token) is ResourceDeploymentMutationError)
                        return tx.Abort(new ForgetRetiredArchiveOutcome(new ArchivePlacementConflict()));
                }

                await outbox(tx.Session, [BackupConfigurationNotifications.ArchiveForgotten(archive)], token);
                return tx.Commit(new ForgetRetiredArchiveOutcome(new BackupArchiveForgotten()));
            },
            ct);
    }

    private static bool StillApplying(BackupPolicy? policy, string archiveId) => policy is not null &&
        (policy.ArchiveId == archiveId || policy.AppliedRevision != policy.Revision);
}
