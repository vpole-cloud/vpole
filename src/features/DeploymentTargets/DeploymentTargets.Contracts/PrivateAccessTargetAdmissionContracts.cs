using MongoDB.Driver;

namespace Pole.Features.DeploymentTargets.Contracts;

public sealed record PrivateAccessDeploymentTargetReady(
    string DeploymentTargetId,
    long Revision
);

public sealed record PrivateAccessDeploymentTargetUnavailable;

public union FenceDeploymentTargetPrivateAccessOutcome(
    PrivateAccessDeploymentTargetReady,
    PrivateAccessDeploymentTargetUnavailable);

/// <summary>Write-fences one target and verifies its isolation attestation in the caller transaction.</summary>
public delegate Task<FenceDeploymentTargetPrivateAccessOutcome> FenceDeploymentTargetPrivateAccess(
    string deploymentTargetId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
