// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Queries;

internal sealed class MongoLoadResourceDeployment(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    ResourceDeploymentIsolationProjection isolation
)
{
    public async Task<PlacementSummaryDto?> InvokeAsync(
        LoadResourceDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var deployment = await resourceDeployments
            .Find(ResourceDeploymentFilters.ForResource(request.TenantId, request.ResourceType, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        return deployment is null ? null : await isolation.ProjectAsync(deployment, cancellationToken);
    }
}
