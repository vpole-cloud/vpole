using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Common;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Execution;

public sealed record BackupArchiveForgotten;

public sealed record BackupArchiveMissing;

public sealed record BackupArchiveInUse;

public sealed record ArchivePlacementConflict;

public sealed record ArchiveObservationUnavailable;

public union ForgetRetiredArchiveOutcome(
    BackupArchiveForgotten,
    BackupArchiveMissing,
    BackupArchiveInUse,
    ArchivePlacementConflict,
    ArchiveObservationUnavailable);

public sealed class ForgetRetiredArchive(
    IMongoCollection<BackupArchive> archives,
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupDestination> destinations,
    IMongoCollection<BackupOperation> operations,
    MongoRunTransaction transactions,
    LoadResourceDeploymentPlanningSnapshot placements,
    RequestExactResourceDeploymentDeletion requestDeletion,
    LoadRegisteredResource loadSource,
    BackupProviderCatalog providers,
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
        // The operator can finish pre-existing base backups after its schedule is suspended.
        // Detect such work on the target before releasing its credentials. No S3 access is required.
        if (await loadSource(
                new(tenantId, ResourceTypeId.Create(selected.ResourceType), selected.ResourceId),
                ct) is not null)
        {
            var provider = providers.Find(selected.ResourceType);
            if (provider is null ||
                await provider.ObserveAsync(BackupArchiveAccess.Context(selected), ct) is not
                    BackupProviderResult<BackupRuntimeObservation>.Success observation)
                return new ArchiveObservationUnavailable();
            if (observation.Value.Runs.Any(run => run.State == BackupObservedRunState.Running))
                return new BackupArchiveInUse();
        }

        return await transactions.InvokeOutcomeAsync<ForgetRetiredArchiveOutcome>(
            async (tx, token) =>
            {
                var archive = await archives.Find(tx.Session, filter).SingleOrDefaultAsync(token);
                if (archive is null)
                    return tx.Abort(new ForgetRetiredArchiveOutcome(new BackupArchiveMissing()));
                if (archive.Active || await operations.Find(
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
