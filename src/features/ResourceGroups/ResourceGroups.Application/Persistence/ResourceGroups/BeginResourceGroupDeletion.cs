using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Persistence.RuntimeCells;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceRegistrations;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;

public sealed record ResourceGroupDeletionCompleted;

public sealed record ResourceGroupDeletionRequested;

public union ResourceGroupDeletionError(
    ResourceGroupNotFound,
    ResourceGroupContainsResources,
    ResourceGroupDeploymentInProgress,
    ResourceGroupRevisionConflict);

public union BeginResourceGroupDeletionOutcome(
    ResourceGroupDeletionCompleted,
    ResourceGroupDeletionRequested,
    ResourceGroupDeletionError);

public delegate Task<BeginResourceGroupDeletionOutcome> BeginResourceGroupDeletion(
    string tenantId,
    string resourceGroupId,
    CancellationToken cancellationToken = default);

internal sealed class MongoBeginResourceGroupDeletion(
    IMongoCollection<ResourceGroup> resourceGroups,
    IMongoCollection<IsolationScope> isolationScopes,
    IMongoCollection<RuntimeCell> runtimeCells,
    IMongoCollection<ResourceRegistration> resourceRegistrations,
    EnsureNoUnfinishedResourceGroupDeployments ensureNoUnfinishedDeployments,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<BeginResourceGroupDeletionOutcome> runMongoTransaction,
    TimeProvider timeProvider)
{
    public Task<BeginResourceGroupDeletionOutcome> InvokeAsync(
        string tenantId,
        string resourceGroupId,
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
                return transaction.Abort(new BeginResourceGroupDeletionOutcome(
                    new ResourceGroupDeletionError(
                        new ResourceGroupNotFound(resourceGroupId))));
            }
            if (resourceGroup.Lifecycle is not ActiveResourceGroupLifecycleState &&
                resourceGroup.Lifecycle is not DeletingResourceGroupLifecycleState)
            {
                throw new InvalidOperationException(
                    $"Resource group '{resourceGroupId}' has an unsupported lifecycle state.");
            }
            if (await resourceRegistrations
                .Find(
                    transaction.Session,
                    ResourceRegistrationFilters.ByResourceGroup(tenantId, resourceGroupId))
                .AnyAsync(ct))
            {
                return transaction.Abort(new BeginResourceGroupDeletionOutcome(
                    new ResourceGroupDeletionError(
                        new ResourceGroupContainsResources(resourceGroupId))));
            }

            var deploymentGuard = await ensureNoUnfinishedDeployments(
                tenantId,
                resourceGroupId,
                transaction.Session,
                ct);
            if (deploymentGuard is Err<ResourceGroupHasUnfinishedDeployments>)
            {
                return transaction.Abort(new BeginResourceGroupDeletionOutcome(
                    new ResourceGroupDeletionError(
                        new ResourceGroupDeploymentInProgress(resourceGroupId))));
            }

            var scopes = await isolationScopes
                .Find(
                    transaction.Session,
                    IsolationScopeFilters.ByTenantAndResourceGroup(tenantId, resourceGroupId))
                .ToListAsync(ct);
            var cells = await runtimeCells
                .Find(
                    transaction.Session,
                    RuntimeCellFilters.ByTenantAndResourceGroup(tenantId, resourceGroupId))
                .ToListAsync(ct);
            if (cells.All(cell => cell.State is DeletedRuntimeCellState))
            {
                await runtimeCells.DeleteManyAsync(
                    transaction.Session,
                    RuntimeCellFilters.ByTenantAndResourceGroup(tenantId, resourceGroupId),
                    cancellationToken: ct);
                await isolationScopes.DeleteManyAsync(
                    transaction.Session,
                    IsolationScopeFilters.ByTenantAndResourceGroup(tenantId, resourceGroupId),
                    cancellationToken: ct);
                var deletion = await resourceGroups.DeleteOneAsync(
                    transaction.Session,
                    ResourceGroupFilters.AtRevisionAndRegistrationFence(resourceGroup),
                    cancellationToken: ct);
                if (deletion.DeletedCount != 1)
                {
                    return transaction.Abort(new BeginResourceGroupDeletionOutcome(
                        new ResourceGroupDeletionError(
                            new ResourceGroupRevisionConflict(resourceGroupId))));
                }
                await storeOutboxWrites(
                    transaction.Session,
                    [OutboxWrite.Create(resourceGroup.ToDeleteEvent())],
                    ct);
                return transaction.Commit(new BeginResourceGroupDeletionOutcome(
                    new ResourceGroupDeletionCompleted()));
            }

            var requestedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            if (resourceGroup.Lifecycle is ActiveResourceGroupLifecycleState)
            {
                var deletingGroup = resourceGroup.RequestDeletion(requestedAtUtc);
                var write = await resourceGroups.ReplaceOneAsync(
                    transaction.Session,
                    ResourceGroupFilters.ActiveAtRevisionAndRegistrationFence(resourceGroup),
                    deletingGroup,
                    cancellationToken: ct);
                if (write.ModifiedCount != 1)
                {
                    return transaction.Abort(new BeginResourceGroupDeletionOutcome(
                        new ResourceGroupDeletionError(
                            new ResourceGroupRevisionConflict(resourceGroupId))));
                }
            }
            var outboxWrites = new List<OutboxWrite>();
            foreach (var scope in scopes)
            {
                if (scope.Lifecycle is DeletingIsolationScopeLifecycleState)
                    continue;

                var deletingScope = scope.RequestResourceGroupDeletion(requestedAtUtc);
                var write = await isolationScopes.ReplaceOneAsync(
                    transaction.Session,
                    IsolationScopeFilters.AtRevision(
                        tenantId,
                        resourceGroupId,
                        scope.Id,
                        scope.Revision),
                    deletingScope,
                    cancellationToken: ct);
                if (write.ModifiedCount != 1)
                {
                    return transaction.Abort(new BeginResourceGroupDeletionOutcome(
                        new ResourceGroupDeletionError(
                            new ResourceGroupRevisionConflict(resourceGroupId))));
                }

                outboxWrites.Add(OutboxWrite.Create(deletingScope.ToDeletionRequestedEvent()));
            }

            foreach (var cell in cells)
            {
                if (cell.State is DeletedRuntimeCellState or DeletingRuntimeCellState)
                    continue;

                var deletingCell = cell.RequestDeletion(requestedAtUtc);
                var write = await runtimeCells.ReplaceOneAsync(
                    transaction.Session,
                    RuntimeCellFilters.AtDesiredGeneration(cell),
                    deletingCell,
                    cancellationToken: ct);
                if (write.ModifiedCount != 1)
                {
                    return transaction.Abort(new BeginResourceGroupDeletionOutcome(
                        new ResourceGroupDeletionError(
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

            return transaction.Commit(new BeginResourceGroupDeletionOutcome(
                new ResourceGroupDeletionRequested()));
        }, cancellationToken);
}
