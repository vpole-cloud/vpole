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
        var hasUnfinished = await HasUnfinishedAsync(
            Builders<ResourceDeployment>.Filter.And(
                ResourceDeploymentFilters.VisibleToTenant(tenantId),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.ResourceGroupId,
                    resourceGroupId)),
            session,
            cancellationToken);
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
        var hasUnfinished = await HasUnfinishedAsync(
            Builders<ResourceDeployment>.Filter.And(
                ResourceDeploymentFilters.VisibleToTenant(tenantId),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.ResourceGroupId,
                    resourceGroupId),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.IsolationScopeId,
                    isolationScopeId)),
            session,
            cancellationToken);
        return hasUnfinished
            ? new Err<IsolationScopeHasUnfinishedDeployments>(
                new IsolationScopeHasUnfinishedDeployments(
                    resourceGroupId,
                    isolationScopeId))
            : new Ok<Unit>(Unit.Value);
    }

    private async Task<bool> HasUnfinishedAsync(
        FilterDefinition<ResourceDeployment> ownershipFilter,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource deployment reference checks must run inside the logical resource transaction.");
        }

        var unfinishedExists = await resourceDeployments
            .Find(
                session,
                Builders<ResourceDeployment>.Filter.And(
                    ownershipFilter,
                    Builders<ResourceDeployment>.Filter.Not(
                        Builders<ResourceDeployment>.Filter
                            .OfType<ResourceDeploymentState, DeletedResourceDeploymentState>(
                                deployment => deployment.State))))
            .Limit(1)
            .AnyAsync(cancellationToken);
        return unfinishedExists;
    }
}
