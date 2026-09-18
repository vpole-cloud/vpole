using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reads;

internal sealed class MongoLoadResourceDeployments(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    ResourceDeploymentIsolationProjection isolation)
{
    public async Task<IReadOnlyDictionary<string, PlacementSummaryDto>> InvokeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        if (resourceIds.Count == 0)
            return new Dictionary<string, PlacementSummaryDto>();

        var deployments = await resourceDeployments
            .Find(ResourceDeploymentFilters.ForResources(tenantId, resourceType, resourceIds))
            .ToListAsync(cancellationToken);
        var policies = await isolation.LoadAsync(deployments.Select(item => item.ResolvedPlacement.DeploymentTargetId), cancellationToken);
        return deployments
            .GroupBy(deployment => deployment.ResourceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var candidates = group.Take(2).ToArray();
                    return candidates.Length == 1
                        ? candidates[0].ToPlacementSummaryDto() with { Isolation = policies[candidates[0].ResolvedPlacement.DeploymentTargetId] }
                        : throw new InvalidOperationException(
                            $"Single-target resource '{group.Key}' has multiple deployments.");
                },
                StringComparer.Ordinal);
    }
}
