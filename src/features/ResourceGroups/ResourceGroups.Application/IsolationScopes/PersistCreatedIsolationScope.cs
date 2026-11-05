using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Application.Mongo;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.IsolationScopes;

public union PersistCreatedIsolationScopeError(
    ResourceGroupRevisionConflict,
    IsolationScopeNameConflict);

public union PersistCreatedIsolationScopeOutcome(
    IsolationScope,
    PersistCreatedIsolationScopeError);

public delegate Task<PersistCreatedIsolationScopeOutcome>
    PersistCreatedIsolationScope(
        ResourceGroup resourceGroup,
        IsolationScope isolationScope,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedIsolationScope(
    IMongoCollection<ResourceGroup> resourceGroups,
    IMongoCollection<IsolationScope> isolationScopes,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<PersistCreatedIsolationScopeOutcome>
        runMongoTransaction)
{
    public async Task<PersistCreatedIsolationScopeOutcome> InvokeAsync(
        ResourceGroup resourceGroup,
        IsolationScope isolationScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceGroup);
        ArgumentNullException.ThrowIfNull(isolationScope);
        try
        {
            return await runMongoTransaction(async (transaction, ct) =>
            {
                var existing = await isolationScopes
                    .Find(
                        transaction.Session,
                        IsolationScopeFilters.ByTechnicalName(
                            isolationScope.TenantId,
                            isolationScope.NormalizedName))
                    .AnyAsync(ct);
                if (existing)
                {
                    return transaction.Abort(new PersistCreatedIsolationScopeError(
                        new IsolationScopeNameConflict(
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
                    return transaction.Abort(new PersistCreatedIsolationScopeError(
                        new ResourceGroupRevisionConflict(
                            resourceGroup.Id,
                            ResourceGroupRevisionConflictReason.ExpectedRevisionConflict)));
                }

                await isolationScopes.InsertOneAsync(
                    transaction.Session,
                    isolationScope,
                    cancellationToken: ct);
                await storeOutboxWrites(
                    transaction.Session,
                    [OutboxWrite.Create(isolationScope.ToCreatedEvent())],
                    ct);
                return transaction.Commit(isolationScope);
            }, cancellationToken);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(exception,
            IsolationScopeIndexes.TenantName, isolationScopes.CollectionNamespace.CollectionName))
        {
            return new PersistCreatedIsolationScopeError(new IsolationScopeNameConflict(
                isolationScope.ResourceGroupId, isolationScope.Name));
        }
    }
}
