using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

internal sealed class MongoGetExactResourceDeploymentFinalizationReadiness(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<ResourceDeploymentFinalizationReadiness> InvokeAsync(
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
        var state = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForExactDeployment(
                    tenantId,
                    resourceType,
                    resourceId,
                    resourceDeploymentId))
            .Project(deployment => deployment.State)
            .FirstOrDefaultAsync(cancellationToken);
        return state switch
        {
            null => ResourceDeploymentFinalizationReadiness.Missing,
            DeletedResourceDeploymentState => ResourceDeploymentFinalizationReadiness.Ready,
            _ => ResourceDeploymentFinalizationReadiness.Waiting
        };
    }
}
