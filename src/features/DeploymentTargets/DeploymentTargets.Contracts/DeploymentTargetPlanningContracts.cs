using MongoDB.Driver;

namespace Pole.Features.DeploymentTargets.Contracts;

public sealed record DeploymentTargetPlanningSnapshot(
    DeploymentTargetAdmissionRequest Request,
    DeploymentTargetLookupDto Target
);

public sealed record DeploymentTargetPlanningUnavailable(
    DeploymentTargetNewWorkloadAdmissionError Error
);

public sealed record DeploymentTargetPlanningFenced;

public sealed record DeploymentTargetPlanningFenceRejected;

public union ResolveDeploymentTargetPlanningSnapshotOutcome(
    DeploymentTargetPlanningSnapshot,
    DeploymentTargetPlanningUnavailable);

public union FenceDeploymentTargetPlanningSnapshotOutcome(
    DeploymentTargetPlanningFenced,
    DeploymentTargetPlanningFenceRejected);

public delegate Task<ResolveDeploymentTargetPlanningSnapshotOutcome> ResolveDeploymentTargetPlanningSnapshot(
    DeploymentTargetAdmissionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

/// <summary>
///     Takes real write fences without changing business revisions. A rejected guard requires
///     the caller to abort the entire transaction, including any preceding guards.
/// </summary>
public delegate Task<FenceDeploymentTargetPlanningSnapshotOutcome> FenceDeploymentTargetPlanningSnapshot(
    DeploymentTargetPlanningSnapshot request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
