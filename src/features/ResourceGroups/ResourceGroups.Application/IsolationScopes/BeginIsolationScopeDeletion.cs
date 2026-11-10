using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.IsolationScopes;

public sealed record IsolationScopeDeletionCompleted(
    string IsolationScopeId
);

public sealed record IsolationScopeDeletionRequested(
    string IsolationScopeId
);

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

public sealed record BeginIsolationScopeDeletionRequest(
    string TenantId,
    string ResourceGroupId,
    string IsolationScopeId,
    GitLifecycleAuthority? Lifecycle = null
);

internal sealed class MongoBeginIsolationScopeDeletion(
    IMongoCollection<ResourceGroup> resourceGroups,
    IMongoCollection<IsolationScope> isolationScopes,
    IMongoCollection<RuntimeCell> runtimeCells,
    EnsureNoUnfinishedIsolationScopeDeployments ensureNoUnfinishedDeployments,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<BeginIsolationScopeDeletionOutcome> runMongoTransaction,
    TimeProvider timeProvider,
    IEnumerable<IIsolationScopeReferenceGuard> referenceGuards,
    IGitOpsMutations git
)
{
    public Task<BeginIsolationScopeDeletionOutcome> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        CancellationToken cancellationToken = default) => runMongoTransaction(
        async (transaction, ct) =>
        {
            var outcome = await ExecuteAsync(
                new BeginIsolationScopeDeletionRequest(tenantId, resourceGroupId, isolationScopeId),
                transaction.Session,
                ct);
            return outcome is IsolationScopeDeletionCompleted or IsolationScopeDeletionRequested
                ? transaction.Commit(outcome)
                : transaction.Abort(outcome);
        },
        cancellationToken);

    internal async Task<BeginIsolationScopeDeletionOutcome> ExecuteAsync(
        BeginIsolationScopeDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("IsolationScope deletion requires a caller-owned transaction.");

        var resourceGroup = await resourceGroups.Find(
                session,
                ResourceGroupFilters.ByTenantAndId(request.TenantId, request.ResourceGroupId))
            .FirstOrDefaultAsync(ct);
        if (resourceGroup is null)
            return new BeginIsolationScopeDeletionOutcome(
                new IsolationScopeDeletionError(new ResourceGroupNotFound(request.ResourceGroupId)));

        if (resourceGroup.Lifecycle is DeletingResourceGroupLifecycleState)
            return new BeginIsolationScopeDeletionOutcome(
                new IsolationScopeDeletionError(new ResourceGroupDeleting(request.ResourceGroupId)));

        if (resourceGroup.Lifecycle is not ActiveResourceGroupLifecycleState)
            throw new InvalidOperationException(
                $"Resource group '{request.ResourceGroupId}' has an unsupported lifecycle state.");

        var scope = await isolationScopes.Find(
                session,
                IsolationScopeFilters.ByTenantResourceGroupAndId(
                    request.TenantId,
                    request.ResourceGroupId,
                    request.IsolationScopeId))
            .FirstOrDefaultAsync(ct);
        if (scope is null)
            return new BeginIsolationScopeDeletionOutcome(
                new IsolationScopeDeletionError(new IsolationScopeNotFound(request.IsolationScopeId)));

        if (scope.IsDefault)
            return new BeginIsolationScopeDeletionOutcome(
                new IsolationScopeDeletionError(new DefaultIsolationScopeDeletionForbidden(request.IsolationScopeId)));

        if (scope.Lifecycle is not ActiveIsolationScopeLifecycleState &&
            scope.Lifecycle is not DeletingIsolationScopeLifecycleState)
            throw new InvalidOperationException($"Isolation scope '{scope.Id}' has an unsupported lifecycle state.");

        if (!await git.AuthorizeAsync(
                new AuthorizeGitResourceMutationRequest(
                    request.TenantId,
                    "IsolationScope",
                    request.IsolationScopeId,
                    Lifecycle: request.Lifecycle,
                    LifecycleEffect: GitLifecycleMutationEffect.Remove),
                session,
                ct))
            return new BeginIsolationScopeDeletionOutcome(
                new IsolationScopeDeletionError(
                    new ResourceGroupRevisionConflict(
                        request.ResourceGroupId,
                        ResourceGroupRevisionConflictReason.GitOwnedConfiguration)));

        foreach (var guard in referenceGuards)
        {
            var reference = await guard.EnsureUnreferencedAsync(
                new EnsureIsolationScopeUnreferencedRequest(
                    TenantId: request.TenantId,
                    ResourceGroupId: request.ResourceGroupId,
                    IsolationScopeId: request.IsolationScopeId),
                session: session,
                cancellationToken: ct);
            if (reference is IsolationScopeReferenced referenced)
                return new BeginIsolationScopeDeletionOutcome(new IsolationScopeDeletionError(referenced));
        }

        var deploymentGuard = await ensureNoUnfinishedDeployments(
            new EnsureNoUnfinishedIsolationScopeDeploymentsRequest(
                TenantId: request.TenantId,
                ResourceGroupId: request.ResourceGroupId,
                IsolationScopeId: request.IsolationScopeId),
            session: session,
            cancellationToken: ct);
        if (deploymentGuard is IsolationScopeHasUnfinishedDeployments)
            return new BeginIsolationScopeDeletionOutcome(
                new IsolationScopeDeletionError(new IsolationScopeDeploymentInProgress(request.IsolationScopeId)));

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var fence = await resourceGroups.UpdateOneAsync(
            session,
            ResourceGroupFilters.ActiveAtRevision(request.TenantId, request.ResourceGroupId, resourceGroup.Revision),
            Builders<ResourceGroup>.Update.Inc(group => group.Revision, 1).Set(group => group.UpdatedAtUtc, now),
            cancellationToken: ct);
        if (fence.ModifiedCount != 1)
            return new BeginIsolationScopeDeletionOutcome(
                new IsolationScopeDeletionError(
                    new ResourceGroupRevisionConflict(
                        request.ResourceGroupId,
                        ResourceGroupRevisionConflictReason.ExpectedRevisionConflict)));

        var cells = await runtimeCells.Find(
                session,
                RuntimeCellFilters.ByTenantResourceGroupAndScope(
                    request.TenantId,
                    request.ResourceGroupId,
                    request.IsolationScopeId))
            .ToListAsync(ct);
        if (cells.All(cell => cell.State is DeletedRuntimeCellState))
        {
            var deletingScope = scope.RequestDeletion(now);
            await runtimeCells.DeleteManyAsync(
                session,
                RuntimeCellFilters.ByTenantResourceGroupAndScope(
                    request.TenantId,
                    request.ResourceGroupId,
                    request.IsolationScopeId),
                cancellationToken: ct);
            await isolationScopes.DeleteOneAsync(
                session,
                IsolationScopeFilters.ByTenantResourceGroupAndId(
                    request.TenantId,
                    request.ResourceGroupId,
                    request.IsolationScopeId),
                cancellationToken: ct);
            await storeOutboxWrites(session, [OutboxWrite.Create(deletingScope.ToDeletionRequestedEvent())], ct);
            return new BeginIsolationScopeDeletionOutcome(
                new IsolationScopeDeletionCompleted(request.IsolationScopeId));
        }

        var deleting = scope.RequestDeletion(now);
        var scopeWrite = await isolationScopes.ReplaceOneAsync(
            session,
            IsolationScopeFilters.AtRevision(
                request.TenantId,
                request.ResourceGroupId,
                request.IsolationScopeId,
                scope.Revision),
            deleting,
            cancellationToken: ct);
        if (scopeWrite.ModifiedCount != 1)
            return new BeginIsolationScopeDeletionOutcome(
                new IsolationScopeDeletionError(
                    new ResourceGroupRevisionConflict(
                        request.ResourceGroupId,
                        ResourceGroupRevisionConflictReason.ExpectedRevisionConflict)));

        var outboxWrites = new List<OutboxWrite>();
        if (scope.Lifecycle is not DeletingIsolationScopeLifecycleState)
            outboxWrites.Add(OutboxWrite.Create(deleting.ToDeletionRequestedEvent()));

        foreach (var cell in cells)
        {
            if (cell.State is DeletedRuntimeCellState or DeletingRuntimeCellState)
                continue;

            var deletingCell = cell.RequestDeletion(now);
            var write = await runtimeCells.ReplaceOneAsync(
                session,
                RuntimeCellFilters.AtDesiredGeneration(cell),
                deletingCell,
                cancellationToken: ct);
            if (write.ModifiedCount != 1)
                return new BeginIsolationScopeDeletionOutcome(
                    new IsolationScopeDeletionError(
                        new ResourceGroupRevisionConflict(
                            request.ResourceGroupId,
                            ResourceGroupRevisionConflictReason.ExpectedRevisionConflict)));

            outboxWrites.Add(
                OutboxWrite.Create(
                    deletingCell.ToDeletionRequestedEvent(),
                    RuntimeCellPendingKey.Deletion(deletingCell.Id, deletingCell.DesiredGeneration)));
        }

        if (outboxWrites.Count > 0)
            await storeOutboxWrites(session, outboxWrites, ct);

        return new BeginIsolationScopeDeletionOutcome(new IsolationScopeDeletionRequested(request.IsolationScopeId));
    }
}
