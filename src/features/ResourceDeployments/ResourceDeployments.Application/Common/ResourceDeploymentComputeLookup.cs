using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Common;

public sealed class ResourceDeploymentComputeLookup(
    IResourceDeploymentDbContext dbContext)
    : IResourceDeploymentComputeLookup
{
    public async Task<Result<CurrentResourceDeploymentComputeDto>> GetCurrentAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var deployments = await dbContext.ResourceDeployments
            .Find(ResourceDeploymentFilters.ForResource(
                tenantId,
                resourceType,
                resourceId))
            .SortBy(deployment => deployment.Id)
            .Limit(2)
            .ToListAsync(cancellationToken);
        return deployments.Count switch
        {
            0 => Failures.NotFound(
                "Resource deployment not found.",
                resourceId),
            1 => new CurrentResourceDeploymentComputeDto(
                deployments[0].ResolvedPlacement.DeploymentTargetId,
                deployments[0].ResolvedPlacement.Compute.ToSummaryDto()),
            _ => Failures.Conflict(
                "The logical resource has multiple deployments and does not have a single current compute configuration.")
        };
    }
}
