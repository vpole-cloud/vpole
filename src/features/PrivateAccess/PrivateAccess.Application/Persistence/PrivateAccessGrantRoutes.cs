using Pole.Features.PrivateAccess.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Application.Persistence;

public sealed record PrivateAccessGrantRoute(
    ResourceDeploymentRuntimePlacementDto Placement,
    PrivateAccessResolvedEndpoint Endpoint);

public delegate Task<IReadOnlyDictionary<(string ResourceId, string DeploymentId, string EndpointId),
    PrivateAccessGrantRoute>> LoadPrivateAccessGrantRoutes(
    string tenantId,
    ResourceTypeId resourceType,
    IReadOnlyCollection<PrivateAccessGrant> grants,
    CancellationToken cancellationToken = default);

internal sealed class PrivateAccessGrantRoutes(
    LoadResourceDeploymentRuntimePlacements loadPlacements,
    PrivateAccessEndpointCatalog endpoints)
{
    private const int BatchSize = 256;

    public async Task<IReadOnlyDictionary<(string ResourceId, string DeploymentId, string EndpointId),
        PrivateAccessGrantRoute>> LoadAsync(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<PrivateAccessGrant> grants,
        CancellationToken cancellationToken)
    {
        var grantsByResource = grants.GroupBy(grant => grant.ResourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var resourceIds = grantsByResource.Keys.ToArray();
        var routes = new Dictionary<(string, string, string), PrivateAccessGrantRoute>();
        foreach (var batch in resourceIds.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var placements = await loadPlacements(
                tenantId, resourceType, batch, cancellationToken);
            var applied = placements
                .Where(placement => placement is { Status: ResourceDeploymentStatus.Applied, DesiredOperation: ResourceDeploymentDesiredOperation.Apply })
                .ToDictionary(placement => (placement.ResourceId, placement.DeploymentId));
            var available = await endpoints.LoadAvailableAsync(
                tenantId, resourceType, batch, cancellationToken);
            foreach (var resourceId in batch)
            {
                foreach (var grant in grantsByResource[resourceId])
                {
                    if (!applied.TryGetValue((grant.ResourceId, grant.ResourceDeploymentId),
                            out var placement) ||
                        !available.TryGetValue(grant.ResourceId, out var providerEndpoints))
                        continue;

                    var endpoint = endpoints.ResolveAvailable(providerEndpoints, grant.EndpointId);
                    if (endpoint is not null)
                        routes[(grant.ResourceId, grant.ResourceDeploymentId, grant.EndpointId)] =
                            new PrivateAccessGrantRoute(placement, endpoint);
                }
            }
        }

        return routes;
    }
}
