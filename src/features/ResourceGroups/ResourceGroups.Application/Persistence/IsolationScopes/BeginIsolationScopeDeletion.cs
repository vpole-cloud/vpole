using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Persistence.RuntimeCells;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;

public sealed record IsolationScopeDeletionCompleted(string IsolationScopeId);

public sealed record IsolationScopeDeletionRequested(string IsolationScopeId);

public union IsolationScopeDeletionError(
    ResourceGroupNotFound,
    ResourceGroupDeleting,
    IsolationScopeNotFound,
    DefaultIsolationScopeDeletionForbidden,
    IsolationScopeDeploymentInProgress,
    IsolationScopeReferenced,
    ResourceGroupRevisionConflict);

public union BeginIsolationScopeDeletionOutcome(
    IsolationScopeDeletionCompleted,
    IsolationScopeDeletionRequested,
    IsolationScopeDeletionError);

public delegate Task<BeginIsolationScopeDeletionOutcome> BeginIsolationScopeDeletion(
    string tenantId,
    string resourceGroupId,
    string isolationScopeId,
    CancellationToken cancellationToken = default);

internal sealed class MongoBeginIsolationScopeDeletion(
    IMongoCollection<ResourceGroup> resourceGroups,
    IMongoCollection<IsolationScope> isolationScopes,
    IMongoCollection<RuntimeCell> runtimeCells,
    EnsureNoUnfinishedIsolationScopeDeployments ensureNoUnfinishedDeployments,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<BeginIsolationScopeDeletionOutcome> runMongoTransaction,
    TimeProvider timeProvider,
    IEnumerable<IIsolationScopeReferenceGuard> referenceGuards)
{
    public Task<BeginIsolationScopeDeletionOutcome> InvokeAsync(
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
            {
                return transaction.Abort(new BeginIsolationScopeDeletionOutcome(
                    new IsolationScopeDeletionError(
                        new ResourceGroupNotFound(resourceGroupId))));
            }
            if (resourceGroup.Lifecycle is DeletingResourceGroupLifecycleState)
            {
                return transaction.Abort(new BeginIsolationScopeDeletionOutcome(
                    new IsolationScopeDeletionError(
                        new ResourceGroupDeleting(resourceGroupId))));
            }
            if (resourceGroup.Lifecycle is not ActiveResourceGroupLifecycleState)
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
            if (scope is null)
            {
                return transaction.Abort(new BeginIsolationScopeDeletionOutcome(
                    new IsolationScopeDeletionError(
                        new IsolationScopeNotFound(isolationScopeId))));
            }
            if (scope.IsDefault)
            {
                return transaction.Abort(new BeginIsolationScopeDeletionOutcome(
                    new IsolationScopeDeletionError(
                        new DefaultIsolationScopeDeletionForbidden(isolationScopeId))));
            }
            if (scope.Lifecycle is not ActiveIsolationScopeLifecycleState &&
                scope.Lifecycle is not DeletingIsolationScopeLifecycleState)
            {
                throw new InvalidOperationException(
                    $"Isolation scope '{scope.Id}' has an unsupported lifecycle state.");
            }

            foreach (var guard in referenceGuards)
            {
                var reference = await guard.EnsureUnreferencedAsync(
                    tenantId, resourceGroupId, isolationScopeId, transaction.Session, ct);
                if (reference is Err<IsolationScopeReferenced> referenced)
                    return transaction.Abort(new BeginIsolationScopeDeletionOutcome(
                        new IsolationScopeDeletionError(referenced.Value)));
            }

            var deploymentGuard = await ensureNoUnfinishedDeployments(
                tenantId,
                resourceGroupId,
                isolationScopeId,
                transaction.Session,
                ct);
            if (deploymentGuard is Err<IsolationScopeHasUnfinishedDeployments>)
            {
                return transaction.Abort(new BeginIsolationScopeDeletionOutcome(
                    new IsolationScopeDeletionError(
                        new IsolationScopeDeploymentInProgress(isolationScopeId))));
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var fence = await resourceGroups.UpdateOneAsync(
                transaction.Session,
                ResourceGroupFilters.ActiveAtRevision(
                    tenantId,
                    resourceGroupId,
                    resourceGroup.Revision),
                Builders<ResourceGroup>.Update
                    .Inc(group => group.Revision, 1)
                    .Set(group => group.UpdatedAtUtc, now),
                cancellationToken: ct);
            if (fence.ModifiedCount != 1)
            {
                return transaction.Abort(new BeginIsolationScopeDeletionOutcome(
                    new IsolationScopeDeletionError(
                        new ResourceGroupRevisionConflict(resourceGroupId))));
            }

            var cells = await runtimeCells
                .Find(
                    transaction.Session,
                    RuntimeCellFilters.ByTenantResourceGroupAndScope(
                        tenantId,
                        resourceGroupId,
                        isolationScopeId))
                .ToListAsync(ct);
            if (cells.All(cell => cell.State is DeletedRuntimeCellState))
            {
                var deletingScope = scope.RequestDeletion(now);
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
                await storeOutboxWrites(
                    transaction.Session,
                    [OutboxWrite.Create(deletingScope.ToDeletionRequestedEvent())],
                    ct);
                return transaction.Commit(new BeginIsolationScopeDeletionOutcome(
                    new IsolationScopeDeletionCompleted(isolationScopeId)));
            }

            var deleting = scope.RequestDeletion(now);
            var scopeWrite = await isolationScopes.ReplaceOneAsync(
                transaction.Session,
                IsolationScopeFilters.AtRevision(
                    tenantId,
                    resourceGroupId,
                    isolationScopeId,
                    scope.Revision),
                deleting,
                cancellationToken: ct);
            if (scopeWrite.ModifiedCount != 1)
            {
                return transaction.Abort(new BeginIsolationScopeDeletionOutcome(
                    new IsolationScopeDeletionError(
                        new ResourceGroupRevisionConflict(resourceGroupId))));
            }

            var outboxWrites = new List<OutboxWrite>();
            if (scope.Lifecycle is not DeletingIsolationScopeLifecycleState)
                outboxWrites.Add(OutboxWrite.Create(deleting.ToDeletionRequestedEvent()));

            foreach (var cell in cells)
            {
                if (cell.State is DeletedRuntimeCellState or DeletingRuntimeCellState)
                    continue;

                var deletingCell = cell.RequestDeletion(now);
                var write = await runtimeCells.ReplaceOneAsync(
                    transaction.Session,
                    RuntimeCellFilters.AtDesiredGeneration(cell),
                    deletingCell,
                    cancellationToken: ct);
                if (write.ModifiedCount != 1)
                {
                    return transaction.Abort(new BeginIsolationScopeDeletionOutcome(
                        new IsolationScopeDeletionError(
                            new ResourceGroupRevisionConflict(resourceGroupId))));
                }

                outboxWrites.Add(OutboxWrite.Create(
                    deletingCell.ToDeletionRequestedEvent(),
                    RuntimeCellPendingKey.Deletion(
                        deletingCell.Id,
                        deletingCell.DesiredGeneration)));
            }

            if (outboxWrites.Count > 0)
                await storeOutboxWrites(transaction.Session, outboxWrites, ct);

            return transaction.Commit(new BeginIsolationScopeDeletionOutcome(
                new IsolationScopeDeletionRequested(isolationScopeId)));
        }, cancellationToken);
}
