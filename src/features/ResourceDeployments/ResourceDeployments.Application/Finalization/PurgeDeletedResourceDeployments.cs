using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Finalization;

internal sealed class MongoPurgeDeletedResourceDeployments(
    IMongoCollection<ResourceDeployment> resourceDeployments
)
{
    public async Task<PurgeResourceDeploymentsOutcome> InvokeAsync(
        PurgeDeletedResourceDeploymentsRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(session, "Resource deployment finalization");
        var filter = ResourceDeploymentFilters.ForResource(request.TenantId, request.ResourceType, request.ResourceId);
        var states = await resourceDeployments.Find(session, filter)
            .Project(deployment => deployment.State)
            .ToListAsync(cancellationToken);
        if (states.Count == 0 || states.Any(state => state is not DeletedResourceDeploymentState))
            return new ResourceDeploymentFinalizationConflict(request.ResourceId);

        var deletion = await resourceDeployments.DeleteManyAsync(session, filter, cancellationToken: cancellationToken);
        return deletion.DeletedCount == states.Count
            ? new ResourceDeploymentsPurged()
            : new ResourceDeploymentFinalizationConflict(request.ResourceId);
    }
}
