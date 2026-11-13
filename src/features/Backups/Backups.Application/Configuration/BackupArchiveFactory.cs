using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Configuration;

public sealed class BackupArchiveFactory(
    IMongoCollection<BackupArchive> archives,
    FenceRegisteredResource fenceResource,
    LoadResourceDeploymentPlanningSnapshot loadPlacement,
    FenceResourceDeploymentPlanningSnapshot fencePlacement,
    LoadResourceDeploymentConnectionPlacements namespaces,
    RetainProjectionPlacement retainPlacement,
    TimeProvider clock
)
{
    internal async Task<BackupWriteResult<BackupArchive>> CreateAsync(
        BackupConfigurationWrite<BackupPolicyInput> write,
        BackupDestination destination,
        IBackupProvider provider,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var input = write.Input;
        var type = ResourceTypeId.Create(input.ResourceType);
        var registered = await fenceResource(new(write.TenantId, type, input.ResourceId), session, ct);
        var read = await loadPlacement(new(write.TenantId, type, input.ResourceId), session, ct);
        if (registered is null || registered.ResourceGroupId != destination.ResourceGroupId ||
            read is not ResourceDeploymentPlanningSnapshot { Items.Count: 1 } snapshot)
            return new BackupWriteResult<BackupArchive>.Rejected("source-unavailable");
        var source = snapshot.Items[0];
        if (source.State != ResourceDeploymentPlanningState.Active ||
            source.IsolationScopeId != destination.IsolationScopeId || source.Storage is null ||
            await fencePlacement(snapshot, session, ct) is not ResourceDeploymentPlanningFenced)
            return new BackupWriteResult<BackupArchive>.Rejected("placement-unavailable");
        var physical = (await namespaces(new(write.TenantId, type, input.ResourceId), session, ct)).SingleOrDefault();
        if (physical is null || physical.DeletionRequested)
            return new BackupWriteResult<BackupArchive>.Rejected("placement-unavailable");
        var identity = new BackupSourceIdentity(write.TenantId, type.Value, input.ResourceId);
        var description = await provider.DescribeAsync(identity, ct);
        return description switch
        {
            BackupServiceSnapshot service => await CreateForSourceAsync(service),
            BackupSourceUnavailable => new BackupWriteResult<BackupArchive>.Rejected("source-unavailable"),
            BackupSourceUnsupported => new BackupWriteResult<BackupArchive>.Rejected("unsupported-provider"),
        };

        async Task<BackupWriteResult<BackupArchive>> CreateForSourceAsync(BackupServiceSnapshot service)
        {
            if (service.Revision != source.ResourceRevision)
                return new BackupWriteResult<BackupArchive>.Rejected("source-revision-changed");

            var placements = await namespaces(
                new(write.TenantId, BackupResourceTypes.Destination, destination.Id),
                session,
                ct);
            var placementId = placements
                .SingleOrDefault(p => p.DeploymentTargetId == source.DeploymentTargetId && !p.DeletionRequested)
                ?.DeploymentId;
            if (placementId is null)
            {
                placementId = ObjectId.GenerateNewId().ToString();
                var added = await retainPlacement(
                    new(
                        write.TenantId,
                        BackupResourceTypes.Destination,
                        destination.Id,
                        placementId,
                        type,
                        input.ResourceId,
                        source.DeploymentId),
                    session,
                    ct);
                if (added is not PlacementSummaryDto)
                    return new BackupWriteResult<BackupArchive>.Rejected("destination-placement-unavailable");
            }

            var placement = new ArchivePlacement(
                source.ResourceGroupId,
                source.IsolationScopeId,
                source.DeploymentTargetId,
                physical.RuntimeCellId,
                physical.KubernetesNamespace,
                source.DeploymentId,
                placementId,
                source.Storage.StorageProfileId,
                source.Storage.StorageProfileRevision,
                source.Compute.ComputeProfileId,
                source.Compute.ComputeProfileRevision);
            var id = ObjectId.GenerateNewId().ToString();
            var archive = BackupArchive.Create(
                id,
                write.TenantId,
                write.Id,
                destination.Id,
                new BackupArchiveSource(
                    input.ResourceType,
                    input.ResourceId,
                    registered.DisplayName,
                    service.Revision,
                    service.Version,
                    service.StorageGiB,
                    service.Instances),
                destination.Storage,
                placement,
                clock.GetUtcNow().UtcDateTime);
            await archives.InsertOneAsync(session, archive, cancellationToken: ct);
            return new BackupWriteResult<BackupArchive>.Saved(archive);
        }
    }
}
