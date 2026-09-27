using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Validation;

internal sealed class MongoEnsureNoUnfinishedResourceGroupDeployments(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<EnsureResourceGroupDeploymentsSettledOutcome> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(
            session,
            "Resource deployment reference checks");
        var hasUnfinished = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.UnfinishedForResourceGroup(tenantId, resourceGroupId))
            .Limit(1)
            .AnyAsync(cancellationToken);
        return hasUnfinished
            ? new ResourceGroupHasUnfinishedDeployments(resourceGroupId)
            : new ResourceGroupDeploymentsSettled();
    }
}
