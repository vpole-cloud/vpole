using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Common;

internal sealed class ResourceDeploymentReferenceLookup(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<Result<Unit, ResourceGroupHasUnfinishedDeployments>>
        EnsureNoUnfinishedForResourceGroupAsync(
        string tenantId,
        string resourceGroupId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        var hasUnfinished = await resourceDeployments
            .Find(
                session,
                ResourceDeploymentFilters.UnfinishedForResourceGroup(
                    tenantId,
                    resourceGroupId))
            .Limit(1)
            .AnyAsync(cancellationToken);
        return hasUnfinished
            ? new Err<ResourceGroupHasUnfinishedDeployments>(
                new ResourceGroupHasUnfinishedDeployments(resourceGroupId))
            : new Ok<Unit>(Unit.Value);
    }

    public async Task<Result<Unit, IsolationScopeHasUnfinishedDeployments>>
        EnsureNoUnfinishedForIsolationScopeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
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
                new IsolationScopeHasUnfinishedDeployments(
                    resourceGroupId,
                    isolationScopeId))
            : new Ok<Unit>(Unit.Value);
    }

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource deployment reference checks must run inside the logical resource transaction.");
        }
    }
}
