using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Persistence;

public union PersistCreatedIsolationScopeError(
    ResourceGroupRevisionConflict,
    IsolationScopeNameConflict);

public delegate Task<Result<IsolationScope, PersistCreatedIsolationScopeError>>
    PersistCreatedIsolationScope(
        ResourceGroup resourceGroup,
        IsolationScope isolationScope,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedIsolationScope(
    IMongoCollection<ResourceGroup> resourceGroups,
    IMongoCollection<IsolationScope> isolationScopes,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<Result<IsolationScope, PersistCreatedIsolationScopeError>>
        runMongoTransaction)
{
    public Task<Result<IsolationScope, PersistCreatedIsolationScopeError>> InvokeAsync(
        ResourceGroup resourceGroup,
        IsolationScope isolationScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceGroup);
        ArgumentNullException.ThrowIfNull(isolationScope);
        return runMongoTransaction(async (transaction, ct) =>
        {
            var existing = await isolationScopes
                .Find(
                    transaction.Session,
                    IsolationScopeFilters.ByTenantResourceGroupAndNormalizedName(
                        isolationScope.TenantId,
                        isolationScope.ResourceGroupId,
                        isolationScope.NormalizedName))
                .AnyAsync(ct);
            if (existing)
            {
                return transaction.AbortError<IsolationScope, PersistCreatedIsolationScopeError>(
                    new(new IsolationScopeNameConflict(
                        isolationScope.ResourceGroupId,
                        isolationScope.NormalizedName)));
            }

            var now = isolationScope.CreatedAtUtc;
            var fence = await resourceGroups.UpdateOneAsync(
                transaction.Session,
                ResourceGroupFilters.ActiveAtRevision(
                    resourceGroup.TenantId,
                    resourceGroup.Id,
                    resourceGroup.Revision),
                Builders<ResourceGroup>.Update
                    .Inc(group => group.Revision, 1)
                    .Set(group => group.UpdatedAtUtc, now),
                cancellationToken: ct);
            if (fence.ModifiedCount != 1)
            {
                return transaction.AbortError<IsolationScope, PersistCreatedIsolationScopeError>(
                    new(new ResourceGroupRevisionConflict(resourceGroup.Id)));
            }

            await isolationScopes.InsertOneAsync(
                transaction.Session,
                isolationScope,
                cancellationToken: ct);
            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(isolationScope.ToCreatedEvent())],
                ct);
            return transaction.Commit(new Ok<IsolationScope>(isolationScope));
        }, cancellationToken);
    }
}
