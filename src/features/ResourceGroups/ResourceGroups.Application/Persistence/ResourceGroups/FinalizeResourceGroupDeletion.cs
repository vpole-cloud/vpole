using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Persistence.RuntimeCells;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;

public delegate Task FinalizeResourceGroupDeletion(
    string tenantId,
    string resourceGroupId,
    string isolationScopeId,
    CancellationToken cancellationToken = default);

internal sealed class MongoFinalizeResourceGroupDeletion(
    IMongoCollection<ResourceGroup> resourceGroups,
    IMongoCollection<IsolationScope> isolationScopes,
    IMongoCollection<RuntimeCell> runtimeCells,
    EnsureNoUnfinishedIsolationScopeDeployments ensureNoUnfinishedDeployments,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<Unit> runMongoTransaction,
    TimeProvider timeProvider)
{
    public Task InvokeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (transaction, ct) =>
        {
            var resourceGroup = await resourceGroups
                .Find(
                    transaction.Session,
                    ResourceGroupFilters.ByTenantAndId(tenantId, resourceGroupId))
                .FirstOrDefaultAsync(ct);
            if (resourceGroup is null)
                return transaction.Commit(Unit.Value);
            if (resourceGroup.Lifecycle is not ActiveResourceGroupLifecycleState &&
                resourceGroup.Lifecycle is not DeletingResourceGroupLifecycleState)
            {
                throw new InvalidOperationException(
                    $"Resource group '{resourceGroupId}' has an unsupported lifecycle state.");
            }

            var scope = await isolationScopes
                .Find(
                    transaction.Session,
                IsolationScopeFilters.ByTenantResourceGroupAndId(
                        tenantId,
                        resourceGroupId,
                        isolationScopeId))
                .FirstOrDefaultAsync(ct);
            if (scope is not null &&
                scope.Lifecycle is not ActiveIsolationScopeLifecycleState &&
                scope.Lifecycle is not DeletingIsolationScopeLifecycleState)
            {
                throw new InvalidOperationException(
                    $"Isolation scope '{scope.Id}' has an unsupported lifecycle state.");
            }
            var resourceGroupDeleting =
                resourceGroup.Lifecycle is DeletingResourceGroupLifecycleState;
            var scopeDeleting = scope?.Lifecycle is DeletingIsolationScopeLifecycleState;
            if (!resourceGroupDeleting && !scopeDeleting)
                return transaction.Commit(Unit.Value);

            var deploymentGuard = await ensureNoUnfinishedDeployments(
                tenantId,
                resourceGroupId,
                isolationScopeId,
                transaction.Session,
                ct);
            if (deploymentGuard is Err<IsolationScopeHasUnfinishedDeployments>)
                return transaction.Commit(Unit.Value);

            var fence = await resourceGroups.UpdateOneAsync(
                transaction.Session,
                ResourceGroupFilters.ByTenantAndId(tenantId, resourceGroupId),
                Builders<ResourceGroup>.Update
                    .Inc(group => group.Revision, 1)
                    .Set(group => group.UpdatedAtUtc, timeProvider.GetUtcNow().UtcDateTime),
                cancellationToken: ct);
            if (fence.ModifiedCount != 1)
                return transaction.Commit(Unit.Value);

            if (scope is not null)
            {
                var unfinishedCellExists = await runtimeCells
                    .Find(
                        transaction.Session,
                        RuntimeCellFilters.NotDeleted(
                            tenantId,
                            resourceGroupId,
                            isolationScopeId))
                    .AnyAsync(ct);
                if (unfinishedCellExists)
                    return transaction.Commit(Unit.Value);

                await runtimeCells.DeleteManyAsync(
                    transaction.Session,
                    RuntimeCellFilters.ByTenantResourceGroupAndScope(
                        tenantId,
                        resourceGroupId,
                        isolationScopeId),
                    cancellationToken: ct);
                await isolationScopes.DeleteOneAsync(
                    transaction.Session,
                    IsolationScopeFilters.ByTenantResourceGroupAndId(
                        tenantId,
                        resourceGroupId,
                        isolationScopeId),
                    cancellationToken: ct);
            }

            if (!resourceGroupDeleting)
                return transaction.Commit(Unit.Value);

            var remainingScopeExists = await isolationScopes
                .Find(
                    transaction.Session,
                    IsolationScopeFilters.ByTenantAndResourceGroup(tenantId, resourceGroupId))
                .AnyAsync(ct);
            if (remainingScopeExists)
                return transaction.Commit(Unit.Value);

            var remainingCellExists = await runtimeCells
                .Find(
                    transaction.Session,
                    RuntimeCellFilters.ByTenantAndResourceGroup(tenantId, resourceGroupId))
                .AnyAsync(ct);
            if (remainingCellExists)
                return transaction.Commit(Unit.Value);

            var deletion = await resourceGroups.DeleteOneAsync(
                transaction.Session,
                ResourceGroupFilters.Deleting(tenantId, resourceGroupId),
                cancellationToken: ct);
            if (deletion.DeletedCount == 1)
            {
                await storeOutboxWrites(
                    transaction.Session,
                    [OutboxWrite.Create(resourceGroup.ToDeleteEvent())],
                    ct);
            }

            return transaction.Commit(Unit.Value);
        }, cancellationToken);
}
