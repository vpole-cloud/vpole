using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Contracts;

public enum ResourceConnectionTopologyConflictReason
{
    SourceHasNoPlacements,
    PlacementDeleting,
    DestinationTargetMissing,
    DestinationTargetAmbiguous,
    EndpointMissing,
    DestinationDeleting,
    ConcurrentMutation
}

public sealed record ResourceConnectionTopologyValid(int RefreshedCount);

public sealed record ResourceConnectionTopologyConflict(
    string ConnectionId,
    ResourceConnectionTopologyConflictReason Reason);

public union ReconcileConnectionsForResourceOutcome(
    ResourceConnectionTopologyValid,
    ResourceConnectionTopologyConflict);

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
