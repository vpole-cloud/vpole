using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Finalization;

internal sealed class MongoPurgeExactDeletedResourceDeployment(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<PurgeResourceDeploymentsOutcome> InvokeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(
            session,
            "Resource deployment finalization");
        var deployment = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForExactDeployment(
                    tenantId,
                    resourceType,
                    resourceId,
                    resourceDeploymentId))
            .FirstOrDefaultAsync(cancellationToken);
        if (deployment is null || deployment.State is not DeletedResourceDeploymentState)
        {
            return new ResourceDeploymentFinalizationConflict(resourceId, resourceDeploymentId);
        }

        var deletion = await resourceDeployments.DeleteManyAsync(
            session,
            ResourceDeploymentFilters.ForExactDeploymentAtRevision(
                tenantId,
                resourceType,
                resourceId,
                resourceDeploymentId,
                deployment.Revision),
            cancellationToken: cancellationToken);
        return deletion.DeletedCount == 1
            ? new ResourceDeploymentsPurged()
            : new ResourceDeploymentFinalizationConflict(resourceId, resourceDeploymentId);
    }
}
