// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceConnections.Events;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Connections;

internal sealed record ConnectionResolvedSnapshots(
    IReadOnlyList<ConnectionTargetSnapshot> Snapshots
);

internal union ResolveConnectionSnapshotsOutcome(
    ConnectionResolvedSnapshots,
    ResourceConnectionSourceHasNoPlacements,
    ResourceConnectionDestinationTargetMissing,
    ResourceConnectionDestinationTargetAmbiguous,
    ResourceConnectionEndpointMissing,
    ResourceConnectionDestinationDeletingConflict);

internal union RefreshConnectionOutcome(ResourceConnectionTopologyValid, ResourceConnectionConcurrentMutation);

internal sealed class MongoResourceConnectionMutationProvider(
    IMongoCollection<ResourceConnection> connections,
    LoadResourceDeploymentConnectionPlacements loadPlacements,
    ResourceConnectionEndpointCatalog endpointCatalog,
    LoadResourceRegistrationPlanningSnapshot loadRegistration,
    FenceResourceRegistrationPlanningSnapshot fenceRegistration,
    StoreOutboxWrites storeOutboxWrites,
    TimeProvider timeProvider
)
{
    public async Task<ReconcileConnectionsForResourceOutcome> ReconcileAsync(
        ReconcileConnectionsForResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction(session);
        var affected = await connections.Find(
                session,
                ResourceConnectionFilters.ByResource(request.TenantId, request.ResourceType, request.ResourceId))
            .SortBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var refreshedCount = 0;
        var outbox = new List<OutboxWrite>();
        foreach (var current in affected)
        {
            if (current.Lifecycle is not ActiveResourceConnection active)
                continue;
            var resolved = await ResolveSnapshotsAsync(current, session, cancellationToken);
            ReconcileConnectionsForResourceOutcome resolution = resolved switch
            {
                ConnectionResolvedSnapshots found => await RefreshAsync(
                        current,
                        active,
                        found.Snapshots,
                        session,
                        outbox,
                        cancellationToken) switch
                    {
                        ResourceConnectionTopologyValid refreshed => refreshed,
                        ResourceConnectionConcurrentMutation error => error,
                    },
                ResourceConnectionSourceHasNoPlacements error => error,
                ResourceConnectionDestinationTargetMissing error => error,
                ResourceConnectionDestinationTargetAmbiguous error => error,
                ResourceConnectionEndpointMissing error => error,
                ResourceConnectionDestinationDeletingConflict error => error,
            };
            if (resolution is ResourceConnectionTopologyValid valid)
            {
                refreshedCount += valid.RefreshedCount;
                continue;
            }

            return resolution;
        }

        if (outbox.Count > 0)
            await storeOutboxWrites(session, outbox, cancellationToken);
        return new ResourceConnectionTopologyValid(refreshedCount);
    }

    private async Task<RefreshConnectionOutcome> RefreshAsync(
        ResourceConnection current,
        ActiveResourceConnection active,
        IReadOnlyList<ConnectionTargetSnapshot> snapshots,
        IClientSessionHandle session,
        List<OutboxWrite> outbox,
        CancellationToken cancellationToken)
    {
        if (active.Targets.Count == snapshots.Count && active.Targets
                .Select(item => item.Snapshot)
                .Zip(snapshots)
                .All(pair => pair.First.HasSameResolvedValues(pair.Second)))
            return new ResourceConnectionTopologyValid(0);

        var updated = current.RefreshResolvedTargets(snapshots);
        var result = await connections.ReplaceOneAsync(
            session,
            ResourceConnectionFilters.AtRevision(current.TenantId, current.Id, current.Revision),
            updated,
            cancellationToken: cancellationToken);
        if (result.ModifiedCount != 1)
            return new ResourceConnectionConcurrentMutation(current.Id);

        outbox.Add(
            OutboxWrite.Create(
                new ResourceConnectionReconciliationRequestedEvent(current.Id, updated.Generation)
                {
                    EventCore = EventCore.Create(current.TenantId),
                }));
        return new ResourceConnectionTopologyValid(1);
    }

    public async Task<IReadOnlyList<ResourceConnectionPlanningVersion>> ReadCascadePlanningAsync(
        LoadResourceConnectionCascadePlanningRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        RequireTransaction(session);
        return await connections
            .Find(
                session,
                ResourceConnectionFilters.ByResource(request.TenantId, request.ResourceType, request.ResourceId))
            .SortBy(item => item.Id)
            .Project(item => new ResourceConnectionPlanningVersion(item.Id, item.Revision, item.ReferenceFence))
            .ToListAsync(cancellationToken);
    }

    public async Task<CascadeConnectionsForResourceDeletionOutcome> CascadeAsync(
        CascadeConnectionsForResourceDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        RequireTransaction(session);
        var affected = await connections.Find(
                session,
                ResourceConnectionFilters.ByResource(request.TenantId, request.ResourceType, request.ResourceId))
            .SortBy(item => item.Id)
            .ToListAsync(cancellationToken);
        if (request.ReviewedConnections is { } reviewed)
        {
            var expected = reviewed.OrderBy(item => item.ConnectionId, StringComparer.Ordinal).ToArray();
            var actual = affected.Select(item =>
                    new ResourceConnectionPlanningVersion(item.Id, item.Revision, item.ReferenceFence))
                .ToArray();
            if (!actual.SequenceEqual(expected))
            {
                var conflicting = actual.FirstOrDefault(item => !expected.Contains(item)) ??
                    expected.FirstOrDefault(item => !actual.Contains(item)) ?? expected.FirstOrDefault();
                return new ResourceConnectionCascadeConflict(conflicting?.ConnectionId ?? request.ResourceId);
            }

            // Both connection admission and parent deletion write this registration, preventing an
            // unreviewed connection from appearing between the set read and the parent commit.
            var registration = await loadRegistration(
                new LoadRegisteredResourceRequest(request.TenantId, request.ResourceType, request.ResourceId),
                session,
                cancellationToken);
            if (registration is not null &&
                await fenceRegistration(registration, session, cancellationToken) is not
                    ResourceRegistrationSnapshotFenced)
                return new ResourceConnectionCascadeConflict(request.ResourceId);
        }

        var cascaded = 0;
        var outbox = new List<OutboxWrite>();
        foreach (var current in affected)
        {
            if (current.Lifecycle is DeletingResourceConnection)
            {
                if (request.ReviewedConnections is not null)
                {
                    var fence = await connections.UpdateOneAsync(
                        session,
                        ResourceConnectionFilters.AtPlanningVersion(
                            request.TenantId,
                            new ResourceConnectionPlanningVersion(
                                current.Id,
                                current.Revision,
                                current.ReferenceFence)),
                        Builders<ResourceConnection>.Update.Inc(item => item.ReferenceFence, 1),
                        cancellationToken: cancellationToken);
                    if (fence.ModifiedCount != 1)
                        return new ResourceConnectionCascadeConflict(current.Id);
                }

                continue;
            }

            var updated = current.BeginDeletion(timeProvider.GetUtcNow().UtcDateTime);
            var result = await connections.ReplaceOneAsync(
                session,
                ResourceConnectionFilters.AtPlanningVersion(
                    request.TenantId,
                    new ResourceConnectionPlanningVersion(current.Id, current.Revision, current.ReferenceFence)),
                updated,
                cancellationToken: cancellationToken);
            if (result.ModifiedCount != 1)
                return new ResourceConnectionCascadeConflict(current.Id);
            cascaded++;
            outbox.Add(
                OutboxWrite.Create(
                    new ResourceConnectionDeletionRequestedEvent(current.Id, updated.Generation)
                    {
                        EventCore = EventCore.Create(request.TenantId),
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
        return endpointResult switch
        {
            ResolvedConnectionEndpoint endpoint => await ResolvePlacementsAsync(
                connection,
                sourceType,
                destinationType,
                endpoint,
                session,
                cancellationToken),
            ConnectionDestinationDeleting => new ResourceConnectionDestinationDeletingConflict(connection.Id),
            ConnectionEndpointMissing => new ResourceConnectionEndpointMissing(connection.Id),
            ConnectionEndpointUnsupportedByProvider => new ResourceConnectionEndpointMissing(connection.Id),
        };
    }

    private async Task<ResolveConnectionSnapshotsOutcome> ResolvePlacementsAsync(
        ResourceConnection connection,
        ResourceTypeId sourceType,
        ResourceTypeId destinationType,
        ResolvedConnectionEndpoint endpoint,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var sourcePlacements = (await loadPlacements(
                new LoadResourceDeploymentConnectionPlacementsRequest(
                    TenantId: connection.TenantId,
                    ResourceType: sourceType,
                    ResourceId: connection.SourceResourceId),
                session: session,
                cancellationToken: cancellationToken)).Where(item => !item.DeletionRequested)
            .ToArray();
        var destinationPlacements = (await loadPlacements(
                new LoadResourceDeploymentConnectionPlacementsRequest(
                    TenantId: connection.TenantId,
                    ResourceType: destinationType,
                    ResourceId: connection.DestinationResourceId),
                session: session,
                cancellationToken: cancellationToken)).Where(item => !item.DeletionRequested)
            .ToArray();

        var topology = ResourceConnectionTopology.Evaluate(
            sourcePlacements.Select(item => item.DeploymentTargetId),
            destinationPlacements.Select(item => item.DeploymentTargetId));
        switch (topology.Kind)
        {
            case ConnectionTopologyKind.SourceHasNoPlacements:
                return new ResourceConnectionSourceHasNoPlacements(connection.Id);
            case ConnectionTopologyKind.DestinationTargetMissing:
                return new ResourceConnectionDestinationTargetMissing(connection.Id);
            case ConnectionTopologyKind.DestinationTargetAmbiguous:
                return new ResourceConnectionDestinationTargetAmbiguous(connection.Id);
        }

        var snapshots = new List<ConnectionTargetSnapshot>();
        foreach (var sourcesOnTarget in sourcePlacements.GroupBy(
                     item => item.DeploymentTargetId,
                     StringComparer.Ordinal))
        {
            var destination = destinationPlacements.Single(item => item.DeploymentTargetId == sourcesOnTarget.Key);
            snapshots.Add(
                ConnectionTargetSnapshot.Create(
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
