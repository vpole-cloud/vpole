using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reads;

internal sealed class MongoLoadResourceDeploymentSet(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<IReadOnlyList<PlacementSummaryDto>> InvokeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default) =>
        (await resourceDeployments
            .Find(ResourceDeploymentFilters.ForResource(tenantId, resourceType, resourceId))
            .SortBy(deployment => deployment.Id)
            .ToListAsync(cancellationToken))
        .Select(deployment => deployment.ToPlacementSummaryDto())
        .ToArray();
}
