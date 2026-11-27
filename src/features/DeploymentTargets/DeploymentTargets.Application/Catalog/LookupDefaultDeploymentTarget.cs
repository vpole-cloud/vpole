// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Catalog;

internal sealed class MongoLookupDefaultDeploymentTarget(
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
    LoadDeploymentTargetById loadDeploymentTargetById
)
{
    public async Task<LookupDefaultDeploymentTargetOutcome> InvokeAsync(CancellationToken cancellationToken = default)
    {
        var target = await DeploymentTargetAdmissionSupport.LoadDefaultAsync(
            loadDefaultDeploymentTargetId,
            loadDeploymentTargetById,
            cancellationToken);
        return target switch
        {
            DeploymentTarget value => value.ToLookupDto(),
            DefaultDeploymentTargetLookupError error => error,
        };
    }
}
