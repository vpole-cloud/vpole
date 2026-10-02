using MongoDB.Driver;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Application.Persistence;

internal sealed class PrivateAccessGatewayDesiredStates(
    IMongoCollection<PrivateAccessGrant> grants,
    PrivateAccessGrantRoutes grantRoutes,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<PrivateAccessGatewayDesiredState>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await LoadFilteredAsync(PrivateAccessFilters.ActiveGrants(now), cancellationToken);
    }

    public async Task<IReadOnlyList<PrivateAccessGatewayDesiredState>> LoadForTenantAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await LoadFilteredAsync(
            PrivateAccessFilters.ActiveGrantsForTenant(tenantId, now), cancellationToken);
    }

    private async Task<IReadOnlyList<PrivateAccessGatewayDesiredState>> LoadFilteredAsync(
        FilterDefinition<PrivateAccessGrant> filter,
        CancellationToken cancellationToken)
    {
        var active = await grants.Find(filter)
            .ToListAsync(cancellationToken);
        var routes = new Dictionary<(string TenantId, string TargetId),
            Dictionary<(string DeploymentId, string EndpointId), PrivateAccessGatewayEndpointSnapshot>>();
        foreach (var group in active.GroupBy(grant =>
                     (grant.TenantId, grant.ResourceType)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = await grantRoutes.LoadAsync(
                group.Key.TenantId, ResourceTypeId.Create(group.Key.ResourceType),
                group.ToArray(), cancellationToken);
            foreach (var grant in group)
            {
                if (!available.TryGetValue((grant.ResourceId, grant.ResourceDeploymentId,
                        grant.EndpointId), out var resolved))
                    continue;
                var placement = resolved.Placement;
                var endpoint = resolved.Endpoint;
                var pair = (grant.TenantId, placement.DeploymentTargetId);
                if (!routes.TryGetValue(pair, out var pairRoutes))
                {
                    pairRoutes = new Dictionary<(string, string), PrivateAccessGatewayEndpointSnapshot>();
                    routes.Add(pair, pairRoutes);
                }

                var route = new PrivateAccessGatewayEndpointSnapshot(
                    endpoint.Id, placement.RuntimeCellId, placement.DeploymentId,
                    placement.KubernetesNamespace, endpoint.ServiceName,
                    endpoint.ServicePort, endpoint.TargetPort);
                var key = (placement.DeploymentId, endpoint.Id);
                if (pairRoutes.TryGetValue(key, out var previous) && previous != route)
                    throw new InvalidOperationException("Private Access provider returned inconsistent endpoint placement.");
                pairRoutes[key] = route;
            }
        }

        return routes
            .OrderBy(item => item.Key.TenantId, StringComparer.Ordinal)
            .ThenBy(item => item.Key.TargetId, StringComparer.Ordinal)
            .Select(item => new PrivateAccessGatewayDesiredState(
                item.Key.TenantId,
                item.Key.TargetId,
                item.Value.Values.OrderBy(value => value.ResourceDeploymentId, StringComparer.Ordinal)
                    .ThenBy(value => value.EndpointId, StringComparer.Ordinal).ToArray()))
            .ToArray();
    }
}
