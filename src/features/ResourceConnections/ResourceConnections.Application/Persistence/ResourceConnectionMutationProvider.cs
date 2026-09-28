using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceConnections.Events;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Persistence;

internal sealed record ConnectionResolvedSnapshots(
    IReadOnlyList<ConnectionTargetSnapshot> Snapshots);

internal sealed record ConnectionResolutionConflict(
    ResourceConnectionTopologyConflictReason Reason);

internal union ResolveConnectionSnapshotsOutcome(
    ConnectionResolvedSnapshots,
    ConnectionResolutionConflict);

internal sealed class MongoResourceConnectionMutationProvider(
    IMongoCollection<ResourceConnection> connections,
    LoadResourceDeploymentConnectionPlacements loadPlacements,
    ResourceConnectionEndpointCatalog endpointCatalog,
    StoreOutboxWrites storeOutboxWrites,
    TimeProvider timeProvider)
{
    public async Task<ReconcileConnectionsForResourceOutcome> ReconcileAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction(session);
        var affected = await connections.Find(
                session,
                ResourceConnectionFilters.ByResource(tenantId, resourceType, resourceId))
            .SortBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var refreshedCount = 0;
        var outbox = new List<OutboxWrite>();
        foreach (var current in affected)
        {
            if (current.Lifecycle is not ActiveResourceConnection active)
                continue;
            var resolved = await ResolveSnapshotsAsync(current, session, cancellationToken);
            if (resolved is ConnectionResolutionConflict conflict)
                return new ResourceConnectionTopologyConflict(current.Id, conflict.Reason);
            var snapshots = ((ConnectionResolvedSnapshots)resolved.Value).Snapshots;
            if (active.Targets.Count == snapshots.Count &&
                active.Targets.Select(item => item.Snapshot)
                    .Zip(snapshots)
                    .All(pair => pair.First.HasSameResolvedValues(pair.Second)))
                continue;

            var updated = current.RefreshResolvedTargets(snapshots);
            var result = await connections.ReplaceOneAsync(
                session,
                ResourceConnectionFilters.AtRevision(
                    tenantId,
                    current.Id,
                    current.Revision),
                updated,
                cancellationToken: cancellationToken);
            if (result.ModifiedCount != 1)
            {
                return new ResourceConnectionTopologyConflict(
                    current.Id,
                    ResourceConnectionTopologyConflictReason.ConcurrentMutation);
            }
            refreshedCount++;
            outbox.Add(OutboxWrite.Create(new ResourceConnectionReconciliationRequestedEvent(
                current.Id,
                updated.Generation)
            {
                EventCore = EventCore.Create(tenantId)
            }));
        }
        if (outbox.Count > 0)
            await storeOutboxWrites(session, outbox, cancellationToken);
        return new ResourceConnectionTopologyValid(refreshedCount);
    }

    public async Task<CascadeConnectionsForResourceDeletionOutcome> CascadeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction(session);
        var affected = await connections.Find(
                session,
                ResourceConnectionFilters.ByResource(tenantId, resourceType, resourceId))
            .SortBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var cascaded = 0;
        var outbox = new List<OutboxWrite>();
        foreach (var current in affected)
        {
            if (current.Lifecycle is DeletingResourceConnection)
                continue;
            var updated = current.BeginDeletion(timeProvider.GetUtcNow().UtcDateTime);
            var result = await connections.ReplaceOneAsync(
                session,
                ResourceConnectionFilters.AtRevision(
                    tenantId,
                    current.Id,
                    current.Revision),
                updated,
                cancellationToken: cancellationToken);
            if (result.ModifiedCount != 1)
                return new ResourceConnectionCascadeConflict(current.Id);
            cascaded++;
            outbox.Add(OutboxWrite.Create(new ResourceConnectionDeletionRequestedEvent(
                current.Id,
                updated.Generation)
            {
                EventCore = EventCore.Create(tenantId)
            }));
        }
        if (outbox.Count > 0)
            await storeOutboxWrites(session, outbox, cancellationToken);
        return new ResourceConnectionsCascaded(cascaded);
    }

    private async Task<ResolveConnectionSnapshotsOutcome> ResolveSnapshotsAsync(
        ResourceConnection connection,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var sourceType = ResourceTypeId.Create(connection.SourceResourceTypeId);
        var destinationType = ResourceTypeId.Create(connection.DestinationResourceTypeId);
        var endpointResult = await endpointCatalog.ResolveAsync(
            connection.TenantId,
            destinationType,
            connection.DestinationResourceId,
            connection.EndpointId,
            session,
            cancellationToken);
        if (endpointResult is ConnectionDestinationDeleting)
            return new ConnectionResolutionConflict(
                ResourceConnectionTopologyConflictReason.DestinationDeleting);
        if (endpointResult is not ResolvedConnectionEndpoint endpoint)
            return new ConnectionResolutionConflict(
                ResourceConnectionTopologyConflictReason.EndpointMissing);

        var sourcePlacements = (await loadPlacements(
            connection.TenantId,
            sourceType,
            connection.SourceResourceId,
            session,
            cancellationToken))
            .Where(item => !item.DeletionRequested)
            .ToArray();
        if (sourcePlacements.Length == 0)
            return new ConnectionResolutionConflict(
                ResourceConnectionTopologyConflictReason.SourceHasNoPlacements);
        var destinationPlacements = (await loadPlacements(
            connection.TenantId,
            destinationType,
            connection.DestinationResourceId,
            session,
            cancellationToken))
            .Where(item => !item.DeletionRequested)
            .ToArray();

        var snapshots = new List<ConnectionTargetSnapshot>();
        foreach (var sourcesOnTarget in sourcePlacements
            .GroupBy(item => item.DeploymentTargetId, StringComparer.Ordinal))
        {
            var destinations = destinationPlacements
                .Where(item => item.DeploymentTargetId == sourcesOnTarget.Key)
                .ToArray();
            if (destinations.Length == 0)
                return new ConnectionResolutionConflict(
                    ResourceConnectionTopologyConflictReason.DestinationTargetMissing);
            if (destinations.Length > 1)
                return new ConnectionResolutionConflict(
                    ResourceConnectionTopologyConflictReason.DestinationTargetAmbiguous);
            var destination = destinations[0];
            snapshots.Add(ConnectionTargetSnapshot.Create(
                sourcesOnTarget.Key,
                destination.DeploymentId,
                destination.RuntimeCellId,
                destination.KubernetesNamespace,
                endpoint.ServiceName,
                endpoint.ServicePort.Value,
                endpoint.TargetPort.Value,
                sourcesOnTarget.Select(source => new ConnectionSourcePlacement(
                    source.DeploymentId,
                    source.RuntimeCellId,
                    source.KubernetesNamespace))));
        }
        return new ConnectionResolvedSnapshots(
            snapshots.OrderBy(snapshot => snapshot.TargetId, StringComparer.Ordinal).ToArray());
    }

    private static void RequireTransaction(IClientSessionHandle session)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("A connection mutation requires an active transaction.");
    }
}
