using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Queries;

internal sealed class MongoLoadResourceDeployments(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    ResourceDeploymentIsolationProjection isolation
)
{
    public async Task<IReadOnlyDictionary<string, PlacementSummaryDto>> InvokeAsync(
        LoadResourceDeploymentsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request.ResourceIds);
        if (request.ResourceIds.Count == 0)
            return new Dictionary<string, PlacementSummaryDto>();

        var deployments = await resourceDeployments
            .Find(ResourceDeploymentFilters.ForResources(request.TenantId, request.ResourceType, request.ResourceIds))
            .ToListAsync(cancellationToken);
        var policies = await isolation.LoadAsync(
            deployments.Select(item => item.ResolvedPlacement.DeploymentTargetId),
            cancellationToken);
        return deployments.GroupBy(deployment => deployment.ResourceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var candidates = group.Take(2).ToArray();
                    return candidates.Length == 1
                        ? candidates[0]
                            .ToPlacementSummaryDto(policies[candidates[0].ResolvedPlacement.DeploymentTargetId])
                        : throw new InvalidOperationException(
                            $"Single-target resource '{group.Key}' has multiple deployments.");
                },
                StringComparer.Ordinal);
    }
}
