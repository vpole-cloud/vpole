using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.ResourceGroups;

public sealed record ResourceGroupNameAlreadyExists;

public union PersistCreatedResourceGroupResult(ResourceGroup, ResourceGroupNameAlreadyExists);

public delegate Task<PersistCreatedResourceGroupResult> PersistCreatedResourceGroup(
    ResourceGroup resourceGroup,
    IsolationScope defaultIsolationScope,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedResourceGroup(
    IMongoCollection<ResourceGroup> resourceGroups,
    IMongoCollection<IsolationScope> isolationScopes,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<PersistCreatedResourceGroupResult> runMongoTransaction
)
{
    public Task<PersistCreatedResourceGroupResult> InvokeAsync(
        ResourceGroup resourceGroup,
        IsolationScope defaultIsolationScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceGroup);
        ArgumentNullException.ThrowIfNull(defaultIsolationScope);
        return runMongoTransaction(
            async (transaction, ct) =>
            {
                try
                {
                    await resourceGroups.InsertOneAsync(transaction.Session, resourceGroup, cancellationToken: ct);
                }
                catch (MongoWriteException ex) when (MongoDuplicateKey.IsForIndex(
                                                         ex,
                                                         ResourceGroupsIndexes.TechnicalName))
                {
                    return transaction.Abort(new ResourceGroupNameAlreadyExists());
                }

                await isolationScopes.InsertOneAsync(transaction.Session, defaultIsolationScope, cancellationToken: ct);
                await storeOutboxWrites(transaction.Session, [OutboxWrite.Create(resourceGroup.ToCreateEvent())], ct);
                return transaction.Commit(new PersistCreatedResourceGroupResult(resourceGroup));
            },
            cancellationToken);
    }
}
