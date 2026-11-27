// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Admission;

internal sealed class MongoEnsureNoUnfinishedIsolationScopeDeployments(
    IMongoCollection<ResourceDeployment> resourceDeployments
)
{
    public async Task<EnsureIsolationScopeDeploymentsSettledOutcome> InvokeAsync(
        EnsureNoUnfinishedIsolationScopeDeploymentsRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(session, "Resource deployment reference checks");
        var hasUnfinished = await resourceDeployments.Find(
                session,
                ResourceDeploymentFilters.UnfinishedForIsolationScope(
                    request.TenantId,
                    request.ResourceGroupId,
                    request.IsolationScopeId))
            .Limit(1)
            .AnyAsync(cancellationToken);
        return hasUnfinished
            ? new IsolationScopeHasUnfinishedDeployments(request.ResourceGroupId, request.IsolationScopeId)
            : new IsolationScopeDeploymentsSettled();
    }
}
