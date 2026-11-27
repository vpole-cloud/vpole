// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

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
