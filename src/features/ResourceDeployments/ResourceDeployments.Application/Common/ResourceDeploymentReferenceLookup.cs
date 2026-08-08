using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Common;

public sealed class ResourceDeploymentReferenceLookup(
    IResourceDeploymentDbContext dbContext)
    : IResourceDeploymentReferenceLookup
{
    public Task<Result> EnsureNoUnfinishedForResourceGroupAsync(
        string tenantId,
        string resourceGroupId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        EnsureNoneAsync(
            Builders<ResourceDeployment>.Filter.And(
                ResourceDeploymentFilters.VisibleToTenant(tenantId),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.ResourceGroupId,
                    resourceGroupId)),
            "The resource group contains unfinished resource deployments.",
            session,
            cancellationToken);

    public Task<Result> EnsureNoUnfinishedForIsolationScopeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        EnsureNoneAsync(
            Builders<ResourceDeployment>.Filter.And(
                ResourceDeploymentFilters.VisibleToTenant(tenantId),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.ResourceGroupId,
                    resourceGroupId),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.IsolationScopeId,
                    isolationScopeId)),
            "The isolation scope contains unfinished resource deployments.",
            session,
            cancellationToken);

    private async Task<Result> EnsureNoneAsync(
        FilterDefinition<ResourceDeployment> ownershipFilter,
        string conflictMessage,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var unfinishedExists = await dbContext.ResourceDeployments
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
        return unfinishedExists
            ? Failures.Conflict(conflictMessage)
            : Result.Success();
    }
}
