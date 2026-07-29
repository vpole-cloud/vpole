using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Common;

public sealed class ResourceDeploymentQueryService(
    IResourceDeploymentDbContext dbContext)
    : IResourceDeploymentQueryService
{
    public async Task<PlacementSummaryDto?> GetAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        var deployment = await dbContext.ResourceDeployments
            .Find(BuildFilter(tenantId, resourceType, [resourceId]))
            .FirstOrDefaultAsync(cancellationToken);
        return deployment?.ToPlacementSummaryDto();
    }

    public async Task<IReadOnlyDictionary<string, PlacementSummaryDto>> GetManyAsync(
        string? tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        if (resourceIds.Count == 0)
            return new Dictionary<string, PlacementSummaryDto>();

        var deployments = await dbContext.ResourceDeployments
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

    private static FilterDefinition<ResourceDeployment> BuildFilter(
        string? tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds) =>
        Builders<ResourceDeployment>.Filter.And(
            Builders<ResourceDeployment>.Filter.ForTenant(tenantId),
            Builders<ResourceDeployment>.Filter.Eq(
                deployment => deployment.ResourceType,
                resourceType.Value),
            Builders<ResourceDeployment>.Filter.In(
                deployment => deployment.ResourceId,
                resourceIds));
}
