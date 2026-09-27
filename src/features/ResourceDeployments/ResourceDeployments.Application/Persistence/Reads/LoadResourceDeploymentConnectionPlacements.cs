using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reads;

internal sealed class MongoLoadResourceDeploymentConnectionPlacements(
    IMongoCollection<ResourceDeployment> deployments)
{
    public async Task<IReadOnlyList<ResourceDeploymentConnectionPlacement>> InvokeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Connection placement reads require an active transaction.");

        var found = await deployments
            .Find(session, ResourceDeploymentFilters.ForResource(tenantId, resourceType, resourceId))
            .SortBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return found
            .Select(item => new ResourceDeploymentConnectionPlacement(
                item.Id,
                item.ResolvedPlacement.DeploymentTargetId,
                item.RuntimeCellId,
                item.ResolvedPlacement.KubernetesNamespace,
                item.DesiredOperation == ResourceDeploymentOperation.Delete ||
                    item.State is DeletedResourceDeploymentState))
            .ToArray();
    }
}
