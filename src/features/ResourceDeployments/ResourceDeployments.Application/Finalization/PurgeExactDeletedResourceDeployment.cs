using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Finalization;

internal sealed class MongoPurgeExactDeletedResourceDeployment(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<PurgeResourceDeploymentsOutcome> InvokeAsync(
        PurgeExactDeletedResourceDeploymentRequest request,
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
                    request.TenantId,
                    request.ResourceType,
                    request.ResourceId,
                    request.ResourceDeploymentId))
            .FirstOrDefaultAsync(cancellationToken);
        if (deployment is null || deployment.State is not DeletedResourceDeploymentState)
        {
            return new ResourceDeploymentFinalizationConflict(request.ResourceId, request.ResourceDeploymentId);
        }

        var deletion = await resourceDeployments.DeleteManyAsync(
            session,
            ResourceDeploymentFilters.ForExactDeploymentAtRevision(
                request.TenantId,
                request.ResourceType,
                request.ResourceId,
                request.ResourceDeploymentId,
                deployment.Revision),
            cancellationToken: cancellationToken);
        return deletion.DeletedCount == 1
            ? new ResourceDeploymentsPurged()
            : new ResourceDeploymentFinalizationConflict(request.ResourceId, request.ResourceDeploymentId);
    }
}
