using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reads;

internal sealed class MongoLoadExactResourceDeployment(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    ResourceDeploymentIsolationProjection isolation)
{
    public async Task<PlacementSummaryDto?> InvokeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string deploymentId,
        CancellationToken cancellationToken = default)
    {
        var deployment = await resourceDeployments
            .Find(ResourceDeploymentFilters.ForExactDeployment(
                tenantId,
                resourceType,
                resourceId,
                deploymentId))
            .FirstOrDefaultAsync(cancellationToken);
        return deployment is null ? null : await isolation.ProjectAsync(deployment.ToPlacementSummaryDto(), cancellationToken);
    }
}
