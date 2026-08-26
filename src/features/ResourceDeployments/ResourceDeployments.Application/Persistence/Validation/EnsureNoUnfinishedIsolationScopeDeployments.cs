using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Validation;

internal sealed class MongoEnsureNoUnfinishedIsolationScopeDeployments(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<Result<Unit, IsolationScopeHasUnfinishedDeployments>> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(
            session,
            "Resource deployment reference checks");
        var hasUnfinished = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.UnfinishedForIsolationScope(
                    tenantId,
                    resourceGroupId,
                    isolationScopeId))
            .Limit(1)
            .AnyAsync(cancellationToken);
        return hasUnfinished
            ? new Err<IsolationScopeHasUnfinishedDeployments>(
                new IsolationScopeHasUnfinishedDeployments(resourceGroupId, isolationScopeId))
            : new Ok<Unit>(Unit.Value);
    }
}
