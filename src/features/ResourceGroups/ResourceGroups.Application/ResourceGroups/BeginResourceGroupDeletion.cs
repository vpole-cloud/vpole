using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.ResourceGroups;

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

public sealed record BeginResourceGroupDeletionRequest(
    string TenantId,
    string ResourceGroupId,
    GitLifecycleAuthority? Lifecycle = null
);

internal sealed class MongoBeginResourceGroupDeletion(
    IMongoCollection<ResourceGroup> resourceGroups,
    IMongoCollection<IsolationScope> isolationScopes,
    IMongoCollection<RuntimeCell> runtimeCells,
    IMongoCollection<ResourceRegistration> resourceRegistrations,
    EnsureNoUnfinishedResourceGroupDeployments ensureNoUnfinishedDeployments,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<BeginResourceGroupDeletionOutcome> runMongoTransaction,
    TimeProvider timeProvider,
    IGitOpsMutations git
)
{
    public Task<BeginResourceGroupDeletionOutcome> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default) => runMongoTransaction(
        async (transaction, ct) =>
        {
            var outcome = await ExecuteAsync(
                new BeginResourceGroupDeletionRequest(tenantId, resourceGroupId),
                transaction.Session,
                ct);
            return outcome is ResourceGroupDeletionCompleted or ResourceGroupDeletionRequested
                ? transaction.Commit(outcome)
                : transaction.Abort(outcome);
        },
        cancellationToken);

    internal async Task<BeginResourceGroupDeletionOutcome> ExecuteAsync(
        BeginResourceGroupDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("ResourceGroup deletion requires a caller-owned transaction.");

        var resourceGroup = await resourceGroups.Find(
                session,
                ResourceGroupFilters.ByTenantAndId(request.TenantId, request.ResourceGroupId))
            .FirstOrDefaultAsync(ct);
        if (resourceGroup is null)
            return new BeginResourceGroupDeletionOutcome(
                new ResourceGroupDeletionError(new ResourceGroupNotFound(request.ResourceGroupId)));

        if (!await git.AuthorizeAsync(
                new AuthorizeGitResourceMutationRequest(
                    TenantId: request.TenantId,
                    Kind: "ResourceGroup",
                    ResourceId: request.ResourceGroupId,
                    Lifecycle: request.Lifecycle,
                    LifecycleEffect: GitLifecycleMutationEffect.Remove),
                session: session,
                ct: ct))
            return new BeginResourceGroupDeletionOutcome(
                new ResourceGroupDeletionError(
                    new ResourceGroupRevisionConflict(
                        request.ResourceGroupId,
                        ResourceGroupRevisionConflictReason.GitOwnedConfiguration)));
        if (resourceGroup.Lifecycle is not ActiveResourceGroupLifecycleState &&
            resourceGroup.Lifecycle is not DeletingResourceGroupLifecycleState)
            throw new InvalidOperationException(
                $"Resource group '{request.ResourceGroupId}' has an unsupported lifecycle state.");

        if (await resourceRegistrations.Find(
                    session,
                    ResourceRegistrationFilters.ByResourceGroup(request.TenantId, request.ResourceGroupId))
                .AnyAsync(ct))
            return new BeginResourceGroupDeletionOutcome(
                new ResourceGroupDeletionError(new ResourceGroupContainsResources(request.ResourceGroupId)));

        var deploymentGuard = await ensureNoUnfinishedDeployments(
            new EnsureNoUnfinishedResourceGroupDeploymentsRequest(
                TenantId: request.TenantId,
                ResourceGroupId: request.ResourceGroupId),
            session: session,
            cancellationToken: ct);
        if (deploymentGuard is ResourceGroupHasUnfinishedDeployments)
            return new BeginResourceGroupDeletionOutcome(
                new ResourceGroupDeletionError(new ResourceGroupDeploymentInProgress(request.ResourceGroupId)));

        var scopes = await isolationScopes.Find(
                session,
                IsolationScopeFilters.ByTenantAndResourceGroup(request.TenantId, request.ResourceGroupId))
            .ToListAsync(ct);
        if (request.Lifecycle is not null && scopes.Any(scope => !scope.IsDefault))
            return new BeginResourceGroupDeletionOutcome(
                new ResourceGroupDeletionError(new ResourceGroupContainsResources(request.ResourceGroupId)));

        foreach (var scope in scopes.Where(scope => !scope.IsDefault))
            if (!await git.AuthorizeAsync(
                    new AuthorizeGitResourceMutationRequest(request.TenantId, "IsolationScope", scope.Id),
                    session,
                    ct))
                return new BeginResourceGroupDeletionOutcome(
                    new ResourceGroupDeletionError(
                        new ResourceGroupRevisionConflict(
                            request.ResourceGroupId,
                            ResourceGroupRevisionConflictReason.GitOwnedConfiguration)));

        var cells = await runtimeCells.Find(
                session,
                RuntimeCellFilters.ByTenantAndResourceGroup(request.TenantId, request.ResourceGroupId))
            .ToListAsync(ct);
        if (cells.All(cell => cell.State is DeletedRuntimeCellState))
        {
            await runtimeCells.DeleteManyAsync(
                session,
                RuntimeCellFilters.ByTenantAndResourceGroup(request.TenantId, request.ResourceGroupId),
                cancellationToken: ct);
            await isolationScopes.DeleteManyAsync(
                session,
                IsolationScopeFilters.ByTenantAndResourceGroup(request.TenantId, request.ResourceGroupId),
                cancellationToken: ct);
            var deletion = await resourceGroups.DeleteOneAsync(
                session,
                ResourceGroupFilters.AtRevisionAndRegistrationFence(resourceGroup),
                cancellationToken: ct);
            if (deletion.DeletedCount != 1)
                return new BeginResourceGroupDeletionOutcome(
                    new ResourceGroupDeletionError(
                        new ResourceGroupRevisionConflict(
                            request.ResourceGroupId,
                            ResourceGroupRevisionConflictReason.ExpectedRevisionConflict)));
            await storeOutboxWrites(session, [OutboxWrite.Create(resourceGroup.ToDeleteEvent())], ct);
            return new BeginResourceGroupDeletionOutcome(new ResourceGroupDeletionCompleted());
        }

        var requestedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (resourceGroup.Lifecycle is ActiveResourceGroupLifecycleState)
        {
            var deletingGroup = resourceGroup.RequestDeletion(requestedAtUtc);
            var write = await resourceGroups.ReplaceOneAsync(
                session,
                ResourceGroupFilters.ActiveAtRevisionAndRegistrationFence(resourceGroup),
                deletingGroup,
                cancellationToken: ct);
            if (write.ModifiedCount != 1)
                return new BeginResourceGroupDeletionOutcome(
                    new ResourceGroupDeletionError(
                        new ResourceGroupRevisionConflict(
                            request.ResourceGroupId,
                            ResourceGroupRevisionConflictReason.ExpectedRevisionConflict)));
        }

        var outboxWrites = new List<OutboxWrite>();
        foreach (var scope in scopes)
        {
            if (scope.Lifecycle is DeletingIsolationScopeLifecycleState)
                continue;

            var deletingScope = scope.RequestResourceGroupDeletion(requestedAtUtc);
            var write = await isolationScopes.ReplaceOneAsync(
                session,
                IsolationScopeFilters.AtRevision(request.TenantId, request.ResourceGroupId, scope.Id, scope.Revision),
                deletingScope,
                cancellationToken: ct);
            if (write.ModifiedCount != 1)
                return new BeginResourceGroupDeletionOutcome(
                    new ResourceGroupDeletionError(
                        new ResourceGroupRevisionConflict(
                            request.ResourceGroupId,
                            ResourceGroupRevisionConflictReason.ExpectedRevisionConflict)));

            outboxWrites.Add(OutboxWrite.Create(deletingScope.ToDeletionRequestedEvent()));
        }

        foreach (var cell in cells)
        {
            if (cell.State is DeletedRuntimeCellState or DeletingRuntimeCellState)
                continue;

            var deletingCell = cell.RequestDeletion(requestedAtUtc);
            var write = await runtimeCells.ReplaceOneAsync(
                session,
                RuntimeCellFilters.AtDesiredGeneration(cell),
                deletingCell,
                cancellationToken: ct);
            if (write.ModifiedCount != 1)
                return new BeginResourceGroupDeletionOutcome(
                    new ResourceGroupDeletionError(
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

        return new BeginResourceGroupDeletionOutcome(new ResourceGroupDeletionRequested());
    }
}
