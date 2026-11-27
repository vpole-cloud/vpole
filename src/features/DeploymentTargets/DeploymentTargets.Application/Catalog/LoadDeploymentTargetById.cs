// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Catalog;

public delegate Task<DeploymentTarget?> LoadDeploymentTargetById(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadDeploymentTargetById(
    IMongoCollection<DeploymentTarget> deploymentTargets
)
{
    public async Task<DeploymentTarget?> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) => await deploymentTargets
        .Find(DeploymentTargetFilters.ById(deploymentTargetId))
        .FirstOrDefaultAsync(cancellationToken);
}
