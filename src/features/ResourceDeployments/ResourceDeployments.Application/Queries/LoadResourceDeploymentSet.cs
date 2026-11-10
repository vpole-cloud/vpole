using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Queries;

internal sealed class MongoLoadResourceDeploymentSet(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    ResourceDeploymentIsolationProjection isolation
)
{
    public async Task<IReadOnlyList<PlacementSummaryDto>> InvokeAsync(
        LoadResourceDeploymentSetRequest request,
        CancellationToken cancellationToken = default)
    {
        var deployments = await resourceDeployments
            .Find(ResourceDeploymentFilters.ForResource(request.TenantId, request.ResourceType, request.ResourceId))
            .SortBy(deployment => deployment.Id)
            .ToListAsync(cancellationToken);
        var policies = await isolation.LoadAsync(
            deployments.Select(item => item.ResolvedPlacement.DeploymentTargetId),
            cancellationToken);
        return deployments.Select(deployment =>
                deployment.ToPlacementSummaryDto(policies[deployment.ResolvedPlacement.DeploymentTargetId]))
            .ToArray();
    }
}
