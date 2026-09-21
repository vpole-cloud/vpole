using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reads;

internal sealed class MongoLoadResourceDeploymentSet(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    ResourceDeploymentIsolationProjection isolation)
{
    public async Task<IReadOnlyList<PlacementSummaryDto>> InvokeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var deployments = await resourceDeployments
            .Find(ResourceDeploymentFilters.ForResource(tenantId, resourceType, resourceId))
            .SortBy(deployment => deployment.Id)
            .ToListAsync(cancellationToken);
        var policies = await isolation.LoadAsync(
            deployments.Select(item => item.ResolvedPlacement.DeploymentTargetId),
            cancellationToken);
        return deployments
            .Select(deployment => deployment.ToPlacementSummaryDto() with
            {
                Isolation = policies[deployment.ResolvedPlacement.DeploymentTargetId]
            })
            .ToArray();
    }
}
