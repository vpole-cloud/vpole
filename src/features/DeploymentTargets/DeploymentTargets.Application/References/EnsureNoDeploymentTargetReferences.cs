// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.References;

public sealed record DeploymentTargetUnreferenced;

public union EnsureDeploymentTargetUnreferencedOutcome(
    DeploymentTargetUnreferenced,
    DeploymentTargetHasDurableReferences);

public delegate Task<EnsureDeploymentTargetUnreferencedOutcome> EnsureNoDeploymentTargetReferences(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class MongoEnsureNoDeploymentTargetReferences(
    IMongoCollection<DeploymentTargetReference> references
)
{
    public async Task<EnsureDeploymentTargetUnreferencedOutcome> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var reference = await references.Find(DeploymentTargetReferenceFilters.ForTarget(deploymentTargetId))
            .Project(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return reference is null
            ? new DeploymentTargetUnreferenced()
            : new DeploymentTargetHasDurableReferences(deploymentTargetId);
    }
}
