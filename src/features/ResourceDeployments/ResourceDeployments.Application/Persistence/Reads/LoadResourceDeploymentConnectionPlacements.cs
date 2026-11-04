using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reads;

internal sealed class MongoLoadResourceDeploymentConnectionPlacements(
    IMongoCollection<ResourceDeployment> deployments)
{
    public async Task<IReadOnlyList<ResourceDeploymentConnectionPlacement>> InvokeAsync(
        LoadResourceDeploymentConnectionPlacementsRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Connection placement reads require an active transaction.");

        var found = await deployments
            .Find(session, ResourceDeploymentFilters.ForResource(request.TenantId, request.ResourceType,
                request.ResourceId))
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
