using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Contracts;

public sealed record ResourceConnectionTopologyValid(int RefreshedCount);

public sealed record ResourceConnectionSourceHasNoPlacements(string ConnectionId);
public sealed record ResourceConnectionDestinationTargetMissing(string ConnectionId);
public sealed record ResourceConnectionDestinationTargetAmbiguous(string ConnectionId);
public sealed record ResourceConnectionEndpointMissing(string ConnectionId);
public sealed record ResourceConnectionDestinationDeletingConflict(string ConnectionId);
public sealed record ResourceConnectionConcurrentMutation(string ConnectionId);

public union ReconcileConnectionsForResourceOutcome(
    ResourceConnectionTopologyValid,
    ResourceConnectionSourceHasNoPlacements,
    ResourceConnectionDestinationTargetMissing,
    ResourceConnectionDestinationTargetAmbiguous,
    ResourceConnectionEndpointMissing,
    ResourceConnectionDestinationDeletingConflict,
    ResourceConnectionConcurrentMutation);

/// <summary>
/// Caller has already acquired the resource registration write fence and made
/// its tentative mutation in this transaction. A conflict aborts the caller's
/// whole transaction, including the tentative placement or endpoint change.
/// </summary>
public delegate Task<ReconcileConnectionsForResourceOutcome> ReconcileConnectionsForResource(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public sealed record ResourceConnectionsCascaded(int Count);

public sealed record ResourceConnectionCascadeConflict(string ConnectionId);

public union CascadeConnectionsForResourceDeletionOutcome(
    ResourceConnectionsCascaded,
    ResourceConnectionCascadeConflict);

/// <summary>Marks affected declarations deleting using their retained cleanup snapshots.</summary>
public delegate Task<CascadeConnectionsForResourceDeletionOutcome> CascadeConnectionsForResourceDeletion(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
