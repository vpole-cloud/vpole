using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Persistence;

public delegate Task<ResourceGroup> PersistCreatedResourceGroup(
    ResourceGroup resourceGroup,
    IsolationScope defaultIsolationScope,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedResourceGroup(
    IMongoCollection<ResourceGroup> resourceGroups,
    IMongoCollection<IsolationScope> isolationScopes,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<ResourceGroup> runMongoTransaction)
{
    public Task<ResourceGroup> InvokeAsync(
        ResourceGroup resourceGroup,
        IsolationScope defaultIsolationScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceGroup);
        ArgumentNullException.ThrowIfNull(defaultIsolationScope);
        return runMongoTransaction(async (transaction, ct) =>
        {
            await resourceGroups.InsertOneAsync(
                transaction.Session,
                resourceGroup,
                cancellationToken: ct);
            await isolationScopes.InsertOneAsync(
                transaction.Session,
                defaultIsolationScope,
                cancellationToken: ct);
            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(resourceGroup.ToCreateEvent())],
                ct);
            return transaction.Commit(resourceGroup);
        }, cancellationToken);
    }
}
