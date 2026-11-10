using MongoDB.Driver;

namespace Pole.Features.ResourceGroups.Contracts;

public sealed record ResourceGroupPlanningRequest(
    string TenantId,
    string ResourceGroupId
);

public sealed record ResourceGroupPlanningSnapshot(
    string TenantId,
    string ResourceGroupId,
    long Revision,
    string DefaultIsolationScopeId,
    long ScopeRevision
);

public sealed record ResourceGroupPlanningUnavailable;

public sealed record ResourceGroupPlanningFenced;

public sealed record ResourceGroupPlanningFenceRejected;

public union LoadResourceGroupPlanningSnapshotOutcome(ResourceGroupPlanningSnapshot, ResourceGroupPlanningUnavailable);

public union FenceResourceGroupPlanningSnapshotOutcome(ResourceGroupPlanningFenced, ResourceGroupPlanningFenceRejected);

public delegate Task<LoadResourceGroupPlanningSnapshotOutcome> LoadResourceGroupPlanningSnapshot(
    ResourceGroupPlanningRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

/// <summary>
///     Takes real write fences without changing business revisions. A rejected guard requires
///     the caller to abort the entire transaction, including any preceding guards.
/// </summary>
public delegate Task<FenceResourceGroupPlanningSnapshotOutcome> FenceResourceGroupPlanningSnapshot(
    ResourceGroupPlanningSnapshot request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
