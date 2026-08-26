using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Validation;

internal sealed class MongoEnsureNoUnfinishedResourceGroupDeployments(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<Result<Unit, ResourceGroupHasUnfinishedDeployments>> InvokeAsync(
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
            ? new Err<ResourceGroupHasUnfinishedDeployments>(
                new ResourceGroupHasUnfinishedDeployments(resourceGroupId))
            : new Ok<Unit>(Unit.Value);
    }
}
