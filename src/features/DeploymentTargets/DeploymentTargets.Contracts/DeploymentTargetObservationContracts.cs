using MongoDB.Driver;

namespace Pole.Features.DeploymentTargets.Contracts;

public sealed record DeploymentTargetObservationFenceRequest(
    string DeploymentTargetId,
    long ExpectedConnectionVersion);

public sealed record DeploymentTargetObservationFenced;
public sealed record DeploymentTargetObservationFenceRejected;
public union FenceDeploymentTargetObservationOutcome(
    DeploymentTargetObservationFenced,
    DeploymentTargetObservationFenceRejected);

/// <summary>Fences current enabled target credentials inside the observation owner's transaction.</summary>
public delegate Task<FenceDeploymentTargetObservationOutcome> FenceDeploymentTargetObservation(
    DeploymentTargetObservationFenceRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
