using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

internal sealed class MongoLoadResourceDeploymentSets(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<PlacementSummaryDto>>> InvokeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        if (resourceIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<PlacementSummaryDto>>();

        var deployments = await resourceDeployments
            .Find(ResourceDeploymentFilters.ForResources(tenantId, resourceType, resourceIds))
            .SortBy(deployment => deployment.ResourceId)
            .ThenBy(deployment => deployment.Id)
            .ToListAsync(cancellationToken);
        return deployments
            .GroupBy(deployment => deployment.ResourceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PlacementSummaryDto>)group
                    .Select(deployment => deployment.ToPlacementSummaryDto())
                    .ToArray(),
                StringComparer.Ordinal);
    }
}
