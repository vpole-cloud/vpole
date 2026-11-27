// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Admission;

internal sealed class MongoEnsureNoUnfinishedResourceGroupDeployments(
    IMongoCollection<ResourceDeployment> resourceDeployments
)
{
    public async Task<EnsureResourceGroupDeploymentsSettledOutcome> InvokeAsync(
        EnsureNoUnfinishedResourceGroupDeploymentsRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(session, "Resource deployment reference checks");
        var hasUnfinished = await resourceDeployments.Find(
                session,
                ResourceDeploymentFilters.UnfinishedForResourceGroup(request.TenantId, request.ResourceGroupId))
            .Limit(1)
            .AnyAsync(cancellationToken);
        return hasUnfinished
            ? new ResourceGroupHasUnfinishedDeployments(request.ResourceGroupId)
            : new ResourceGroupDeploymentsSettled();
    }
}
