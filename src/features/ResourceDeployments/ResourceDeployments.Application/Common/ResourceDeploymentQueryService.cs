using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Application.Persistence;

namespace Pole.Features.ResourceDeployments.Application.Common;

internal sealed class ResourceDeploymentQueryService(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<PlacementSummaryDto?> GetAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var deployment = await resourceDeployments
            .Find(BuildFilter(tenantId, resourceType, [resourceId]))
            .FirstOrDefaultAsync(cancellationToken);
        return deployment?.ToPlacementSummaryDto();
    }

    public async Task<IReadOnlyDictionary<string, PlacementSummaryDto>> GetManyAsync(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        if (resourceIds.Count == 0)
            return new Dictionary<string, PlacementSummaryDto>();

        var deployments = await resourceDeployments
            .Find(BuildFilter(tenantId, resourceType, resourceIds))
            .ToListAsync(cancellationToken);
        return deployments
            .GroupBy(deployment => deployment.ResourceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var candidates = group.Take(2).ToArray();
                    return candidates.Length == 1
                        ? candidates[0].ToPlacementSummaryDto()
                        : throw new InvalidOperationException(
                            $"Single-target resource '{group.Key}' has multiple deployments.");
                },
                StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<PlacementSummaryDto>> GetAllAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default) =>
        (await resourceDeployments
            .Find(ResourceDeploymentFilters.ForResource(
                tenantId,
                resourceType,
                resourceId))
            .SortBy(deployment => deployment.Id)
            .ToListAsync(cancellationToken))
        .Select(deployment => deployment.ToPlacementSummaryDto())
        .ToArray();

    public async Task<PlacementSummaryDto?> GetExactAsync(
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
        return deployment?.ToPlacementSummaryDto();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<PlacementSummaryDto>>> GetAllManyAsync(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        if (resourceIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<PlacementSummaryDto>>();

        var deployments = await resourceDeployments
            .Find(BuildFilter(tenantId, resourceType, resourceIds))
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

    private static FilterDefinition<ResourceDeployment> BuildFilter(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds) =>
        ResourceDeploymentFilters.ForResources(
            tenantId,
            resourceType,
            resourceIds);
}
