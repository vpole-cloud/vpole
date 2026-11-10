using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ComputeProfiles.Contracts;

public sealed record ComputeProfilePlanningRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ComputeProfileId,
    string DeploymentTargetId,
    long Revision
);

public sealed record ComputeProfilePlanningSnapshot(
    ComputeProfilePlanningRequest Request,
    long PolicyRevision,
    ResolvedComputeProfile Compute
);

public sealed record ComputeProfilePlanningUnavailable(
    ComputeProfileResolutionError Error
);

public sealed record ComputeProfilePlanningFenced;

public sealed record ComputeProfilePlanningFenceRejected;

public union ResolveComputeProfilePlanningSnapshotOutcome(
    ComputeProfilePlanningSnapshot,
    ComputeProfilePlanningUnavailable);

public union FenceComputeProfilePlanningSnapshotOutcome(
    ComputeProfilePlanningFenced,
    ComputeProfilePlanningFenceRejected);

public delegate Task<ResolveComputeProfilePlanningSnapshotOutcome> ResolveComputeProfilePlanningSnapshot(
    ComputeProfilePlanningRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

/// <summary>
///     Takes real write fences without changing business revisions. A rejected guard requires
///     the caller to abort the entire transaction, including any preceding guards.
/// </summary>
public delegate Task<FenceComputeProfilePlanningSnapshotOutcome> FenceComputeProfilePlanningSnapshot(
    ComputeProfilePlanningSnapshot request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
