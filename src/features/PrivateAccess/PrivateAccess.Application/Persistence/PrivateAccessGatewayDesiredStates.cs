using MongoDB.Driver;
using Pole.Database;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Application.Persistence;

internal sealed record PrivateAccessDesiredEndpoint(PrivateAccessResolvedEndpoint? Endpoint);

internal sealed class PrivateAccessGatewayDesiredStates(
    MongoRunTransaction transactions,
    IMongoCollection<PrivateAccessGrant> grants,
    LoadExactResourceDeploymentRuntimePlacement loadPlacement,
    PrivateAccessEndpointCatalog endpoints,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<PrivateAccessGatewayDesiredState>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var active = await grants.Find(PrivateAccessFilters.ActiveGrants(now))
            .ToListAsync(cancellationToken);
        var routes = new Dictionary<(string TenantId, string TargetId),
            Dictionary<(string DeploymentId, string EndpointId), PrivateAccessGatewayEndpointSnapshot>>();
        foreach (var grant in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resourceType = ResourceTypeId.Create(grant.ResourceType);
            var placement = await loadPlacement(
                grant.TenantId, resourceType, grant.ResourceId,
                grant.ResourceDeploymentId, cancellationToken);
            if (placement is not { Status: "Applied", DesiredOperation: "Apply" })
                continue;

            var resolved = await transactions.InvokeOutcomeAsync<PrivateAccessDesiredEndpoint>(
                async (transaction, ct) =>
                {
                    var endpoint = await endpoints.ResolveAsync(
                        grant.TenantId, resourceType, grant.ResourceId,
                        grant.EndpointId, transaction.Session, ct);
                    return transaction.Abort(new PrivateAccessDesiredEndpoint(endpoint));
                },
                cancellationToken);
            var endpoint = resolved.Endpoint;
            if (endpoint is null)
                continue;

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
