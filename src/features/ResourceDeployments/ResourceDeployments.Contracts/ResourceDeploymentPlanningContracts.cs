using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record ResourceDeploymentPlanningRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId);

public enum ResourceDeploymentPlanningState
{
    Prepared,
    Active,
    Deleting
}

public sealed record ResourceDeploymentPlanningItem(
    string DeploymentId,
    string ResourceGroupId,
    string IsolationScopeId,
    string DeploymentTargetId,
    long Revision,
    int FixedReplicas,
    ResourceDeploymentComputeConfiguration Compute,
    ResourceDeploymentPlanningState State,
    ResourceDeploymentStorageConfiguration? Storage,
    ResourceDeploymentIngressConfiguration? Ingress,
    long DeploymentTargetConfigurationRevision);

public sealed record ResourceDeploymentPlanningSnapshot(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    IReadOnlyList<ResourceDeploymentPlanningItem> Items);

public sealed record ResourceDeploymentPlanningUnavailable;

public sealed record ResourceDeploymentPlanningFenced;

public sealed record ResourceDeploymentPlanningFenceRejected;

public union LoadResourceDeploymentPlanningSnapshotOutcome(
    ResourceDeploymentPlanningSnapshot,
    ResourceDeploymentPlanningUnavailable);

public union FenceResourceDeploymentPlanningSnapshotOutcome(
    ResourceDeploymentPlanningFenced,
    ResourceDeploymentPlanningFenceRejected);

public delegate Task<LoadResourceDeploymentPlanningSnapshotOutcome> LoadResourceDeploymentPlanningSnapshot(
    ResourceDeploymentPlanningRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

/// <summary>
/// Takes real write fences without changing business revisions. A rejected guard requires
/// the caller to abort the entire transaction, including any preceding guards.
/// </summary>
public delegate Task<FenceResourceDeploymentPlanningSnapshotOutcome> FenceResourceDeploymentPlanningSnapshot(
    ResourceDeploymentPlanningSnapshot request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
