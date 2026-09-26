using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reads;

internal sealed class MongoLoadCurrentResourceDeploymentCompute(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<LoadCurrentResourceDeploymentComputeOutcome> InvokeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var deployments = await resourceDeployments
            .Find(ResourceDeploymentFilters.ForResource(tenantId, resourceType, resourceId))
            .SortBy(deployment => deployment.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        return deployments.Count switch
        {
            0 => new LoadCurrentResourceDeploymentComputeError(
                    new CurrentResourceDeploymentComputeNotFound(resourceId)),
            1 => new CurrentResourceDeploymentComputeDto(
                deployments[0].ResolvedPlacement.DeploymentTargetId,
                deployments[0].ResolvedPlacement.Compute.ToSummaryDto()),
            _ => new LoadCurrentResourceDeploymentComputeError(
                    new CurrentResourceDeploymentComputeCardinalityConflict(resourceId))
        };
    }
}
