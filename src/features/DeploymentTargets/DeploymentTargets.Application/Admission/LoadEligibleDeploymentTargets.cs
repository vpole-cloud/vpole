// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Admission;

public delegate Task<IReadOnlyList<DeploymentTarget>> LoadEligibleDeploymentTargets(
    DeploymentTargetRequiredCapabilities requiredCapabilities,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadEligibleDeploymentTargets(
    IMongoCollection<DeploymentTarget> deploymentTargets
)
{
    public async Task<IReadOnlyList<DeploymentTarget>> InvokeAsync(
        DeploymentTargetRequiredCapabilities requiredCapabilities,
        CancellationToken cancellationToken = default)
    {
        var targets = await deploymentTargets.Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .SortBy(target => target.Id)
            .ToListAsync(cancellationToken);
        return targets.Where(target =>
                !requiredCapabilities.HasFlag(DeploymentTargetRequiredCapabilities.PublicIngress) ||
                (target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null &&
                    target.WorkloadConfiguration.NetworkSources.Get(DeploymentTargetNetworkSourcesDto.PublicIngressName)
                        is
                        not null))
            .ToArray();
    }
}
