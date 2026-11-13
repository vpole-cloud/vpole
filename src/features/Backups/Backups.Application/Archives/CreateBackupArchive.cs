using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Providers;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Archives;

public sealed record CreateBackupArchiveRequest(
    string TenantId,
    string PolicyId,
    string ResourceType,
    string ResourceId,
    BackupDestination Destination,
    BackupExecutionOrigin Origin
);

public sealed record BackupSourcePlacementUnavailable;

public sealed record BackupSourceRevisionChanged;

public sealed record BackupDestinationPlacementUnavailable;

public union CreateBackupArchiveOutcome(
    BackupArchive,
    BackupSourceUnavailable,
    BackupSourceUnsupported,
    BackupSourcePlacementUnavailable,
    BackupSourceRevisionChanged,
    BackupDestinationPlacementUnavailable);

public sealed class CreateBackupArchive(
    IMongoCollection<BackupArchive> archives,
    BackupProviderCatalog providers,
    FenceRegisteredResource fenceResource,
    LoadResourceDeploymentPlanningSnapshot loadPlacement,
    FenceResourceDeploymentPlanningSnapshot fencePlacement,
    LoadResourceDeploymentConnectionPlacements namespaces,
    RetainProjectionPlacement retainPlacement,
    TimeProvider clock,
    StoreOutboxWrites outbox
)
{
    internal async Task<CreateBackupArchiveOutcome> CreateAsync(
        CreateBackupArchiveRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var destination = request.Destination;
        var provider = providers.Find(request.ResourceType);
        if (provider is null)
            return new BackupSourceUnsupported();

        var type = ResourceTypeId.Create(request.ResourceType);
        var registered = await fenceResource(new(request.TenantId, type, request.ResourceId), session, ct);
        if (registered is null || registered.ResourceGroupId != destination.ResourceGroupId)
            return new BackupSourceUnavailable();

        var read = await loadPlacement(new(request.TenantId, type, request.ResourceId), session, ct);
        if (read is not ResourceDeploymentPlanningSnapshot { Items.Count: 1 } snapshot)
            return new BackupSourcePlacementUnavailable();

        var source = snapshot.Items[0];
        if (source.State != ResourceDeploymentPlanningState.Active ||
            source.IsolationScopeId != destination.IsolationScopeId || source.Storage is null)
            return new BackupSourcePlacementUnavailable();

        var fenced = await fencePlacement(snapshot, session, ct);
        if (fenced is not ResourceDeploymentPlanningFenced)
            return new BackupSourcePlacementUnavailable();

        var physical
            = (await namespaces(new(request.TenantId, type, request.ResourceId), session, ct)).SingleOrDefault();
        if (physical is null || physical.DeletionRequested)
            return new BackupSourcePlacementUnavailable();

        var identity = new BackupSourceIdentity(request.TenantId, type.Value, request.ResourceId);
        var description = await provider.DescribeAsync(identity, ct);
        return description switch
        {
            BackupServiceSnapshot service => await CreateForSourceAsync(service),
            BackupSourceUnavailable => new BackupSourceUnavailable(),
            BackupSourceUnsupported => new BackupSourceUnsupported(),
        };

        async Task<CreateBackupArchiveOutcome> CreateForSourceAsync(BackupServiceSnapshot service)
        {
            if (service.Revision != source.ResourceRevision)
                return new BackupSourceRevisionChanged();

            var placements = await namespaces(
                new(request.TenantId, BackupResourceTypes.Destination, destination.Id),
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
                        request.TenantId,
                        BackupResourceTypes.Destination,
                        destination.Id,
                        placementId,
                        type,
                        request.ResourceId,
                        source.DeploymentId),
                    session,
                    ct);
                if (added is not PlacementSummaryDto)
                    return new BackupDestinationPlacementUnavailable();
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
                request.TenantId,
                request.PolicyId,
                destination.Id,
                new BackupArchiveSource(
                    request.ResourceType,
                    request.ResourceId,
                    registered.DisplayName,
                    service.Revision,
                    service.Version,
                    service.StorageGiB,
                    service.Instances),
                destination.Storage,
                placement,
                new BackupHistoryStart(id, request.Origin),
                clock.GetUtcNow().UtcDateTime);
            await archives.InsertOneAsync(session, archive, cancellationToken: ct);
            await outbox(session, [BackupWorkRequests.History(archive)], ct);
            return archive;
        }
    }
}
