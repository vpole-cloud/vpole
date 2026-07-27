using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Services;

public interface IResourceGroupDeletionFinalizer
{
    Task<Result> TryFinalizeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        CancellationToken cancellationToken = default);
}

public sealed class ResourceGroupDeletionFinalizer(
    IResourceGroupDbContext dbContext,
    TimeProvider timeProvider)
    : IResourceGroupDeletionFinalizer
{
    public async Task<Result> TryFinalizeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var resourceGroupFilter = Builders<ResourceGroup>.Filter.And(
                Builders<ResourceGroup>.Filter.Eq(group => group.Id, resourceGroupId),
                Builders<ResourceGroup>.Filter.Eq(group => group.TenantId, tenantId));
            var resourceGroup = await dbContext.ResourceGroups
                .Find(session, resourceGroupFilter)
                .FirstOrDefaultAsync(ct);
            if (resourceGroup is null)
                return Result.Success();

            var scopeFilter = Builders<IsolationScope>.Filter.And(
                Builders<IsolationScope>.Filter.Eq(scope => scope.Id, isolationScopeId),
                Builders<IsolationScope>.Filter.Eq(scope => scope.ResourceGroupId, resourceGroupId),
                Builders<IsolationScope>.Filter.Eq(scope => scope.TenantId, tenantId));
            var scope = await dbContext.IsolationScopes
                .Find(session, scopeFilter)
                .FirstOrDefaultAsync(ct);
            var resourceGroupDeleting =
                resourceGroup.Lifecycle is DeletingResourceGroupLifecycleState;
            var scopeDeleting =
                scope?.Lifecycle is DeletingIsolationScopeLifecycleState;
            if (!resourceGroupDeleting && !scopeDeleting)
                return Result.Success();

            var fence = await dbContext.ResourceGroups.UpdateOneAsync(
                session,
                resourceGroupFilter,
                Builders<ResourceGroup>.Update
                    .Inc(group => group.Revision, 1)
                    .Set(group => group.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime),
                cancellationToken: ct);
            if (fence.ModifiedCount != 1)
                return Result.Success();

            if (scope is not null)
            {
                var cellFilter = Builders<RuntimeCell>.Filter.And(
                    Builders<RuntimeCell>.Filter.Eq(cell => cell.TenantId, tenantId),
                    Builders<RuntimeCell>.Filter.Eq(cell => cell.ResourceGroupId, resourceGroupId),
                    Builders<RuntimeCell>.Filter.Eq(cell => cell.IsolationScopeId, isolationScopeId));
                var unfinishedCellExists = await dbContext.RuntimeCells
                    .Find(session, Builders<RuntimeCell>.Filter.And(
                        cellFilter,
                        Builders<RuntimeCell>.Filter.Ne("State._t", "deleted")))
                    .AnyAsync(ct);
                if (unfinishedCellExists)
                    return Result.Success();

                await dbContext.RuntimeCells.DeleteManyAsync(
                    session,
                    cellFilter,
                    cancellationToken: ct);
                await dbContext.IsolationScopes.DeleteOneAsync(
                    session,
                    scopeFilter,
                    cancellationToken: ct);
            }

            if (!resourceGroupDeleting)
                return Result.Success();

            var remainingScopeExists = await dbContext.IsolationScopes
                .Find(session, item =>
                    item.TenantId == tenantId &&
                    item.ResourceGroupId == resourceGroupId)
                .AnyAsync(ct);
            if (remainingScopeExists)
                return Result.Success();

            var remainingCellExists = await dbContext.RuntimeCells
                .Find(session, cell =>
                    cell.TenantId == tenantId &&
                    cell.ResourceGroupId == resourceGroupId)
                .AnyAsync(ct);
            if (remainingCellExists)
                return Result.Success();

            var deletion = await dbContext.ResourceGroups.DeleteOneAsync(
                session,
                Builders<ResourceGroup>.Filter.And(
                    resourceGroupFilter,
                    Builders<ResourceGroup>.Filter.Eq("Lifecycle._t", "deleting")),
                cancellationToken: ct);
            if (deletion.DeletedCount == 1)
                await dbContext.StoreEventAsync(resourceGroup.ToDeleteEvent(), session, ct);

            return Result.Success();
        }, cancellationToken: cancellationToken);
    }
}
