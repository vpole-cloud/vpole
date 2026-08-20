using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Common;

internal sealed class ResourceDeploymentComputeLookup(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<Result<
        CurrentResourceDeploymentComputeDto,
        LoadCurrentResourceDeploymentComputeError>> GetCurrentAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var deployments = await resourceDeployments
            .Find(ResourceDeploymentFilters.ForResource(
                tenantId,
                resourceType,
                resourceId))
            .SortBy(deployment => deployment.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        return deployments.Count switch
        {
            0 => new Err<LoadCurrentResourceDeploymentComputeError>(
                new LoadCurrentResourceDeploymentComputeError(
                    new CurrentResourceDeploymentComputeNotFound(resourceId))),
            1 => new Ok<CurrentResourceDeploymentComputeDto>(new(
                deployments[0].ResolvedPlacement.DeploymentTargetId,
                deployments[0].ResolvedPlacement.Compute.ToSummaryDto())),
            _ => new Err<LoadCurrentResourceDeploymentComputeError>(
                new LoadCurrentResourceDeploymentComputeError(
                    new CurrentResourceDeploymentComputeCardinalityConflict(resourceId)))
        };
    }
}
