using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Finalization;

internal sealed class MongoGetExactResourceDeploymentFinalizationReadiness(
    IMongoCollection<ResourceDeployment> resourceDeployments
)
{
    public async Task<ResourceDeploymentFinalizationReadiness> InvokeAsync(
        GetExactResourceDeploymentFinalizationReadinessRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(session, "Resource deployment finalization");
        var state = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.ForExactDeployment(
                    request.TenantId,
                    request.ResourceType,
                    request.ResourceId,
                    request.ResourceDeploymentId))
            .Project(deployment => deployment.State)
            .FirstOrDefaultAsync(cancellationToken);
        return state switch
        {
            null => ResourceDeploymentFinalizationReadiness.Missing,
            DeletedResourceDeploymentState => ResourceDeploymentFinalizationReadiness.Ready,
            _ => ResourceDeploymentFinalizationReadiness.Waiting,
        };
    }
}
