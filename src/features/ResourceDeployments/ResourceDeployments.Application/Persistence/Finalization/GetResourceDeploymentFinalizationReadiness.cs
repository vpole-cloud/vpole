using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Finalization;

internal sealed class MongoGetResourceDeploymentFinalizationReadiness(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<ResourceDeploymentFinalizationReadiness> InvokeAsync(
        GetResourceDeploymentFinalizationReadinessRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(
            session,
            "Resource deployment finalization");
        var states = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForResource(request.TenantId, request.ResourceType, request.ResourceId))
            .Project(deployment => deployment.State)
            .ToListAsync(cancellationToken);
        if (states.Count == 0)
            return ResourceDeploymentFinalizationReadiness.Missing;

        return states.All(state => state is DeletedResourceDeploymentState)
            ? ResourceDeploymentFinalizationReadiness.Ready
            : ResourceDeploymentFinalizationReadiness.Waiting;
    }
}
