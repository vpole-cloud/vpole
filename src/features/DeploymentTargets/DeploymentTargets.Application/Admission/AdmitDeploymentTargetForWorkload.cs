// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Features.DeploymentTargets.Application.Admission;

internal sealed class MongoAdmitDeploymentTargetForWorkload(
    LoadDeploymentTargetById loadDeploymentTargetById
)
{
    public async Task<AdmitDeploymentTargetForWorkloadOutcome> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var normalizedDeploymentTargetId
            = DeploymentTargetAdmissionSupport.NormalizeDeploymentTargetId(deploymentTargetId);
        var target = await loadDeploymentTargetById(normalizedDeploymentTargetId, cancellationToken);
        return target is null
            ? new DeploymentTargetWorkloadAdmissionError(new DeploymentTargetNotFound(normalizedDeploymentTargetId))
            : DeploymentTargetAdmissionSupport.AdmitForWorkload(target);
    }
}
