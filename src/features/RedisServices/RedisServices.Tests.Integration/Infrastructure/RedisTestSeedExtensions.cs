using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Tests.Integration.Infrastructure;

public static class RedisTestSeedExtensions
{
    public static async Task<ResourceGroup> InsertResourceGroupAsync(this RedisTestStore store, ResourceGroup group)
    {
        var scope = IsolationScopeFactory.CreateDefault(group.TenantId, group.Id,
            new IsolationScopeFactory.CreateOptions(group.DefaultIsolationScopeId, group.CreatedAtUtc));
        _ = await store.RunOutcomeInTransactionAsync<Unit>(async (transaction, cancellationToken) =>
        {
            await store.ResourceGroups.InsertOneAsync(transaction.Session, group, cancellationToken: cancellationToken);
            await store.IsolationScopes.InsertOneAsync(transaction.Session, scope, cancellationToken: cancellationToken);
            return transaction.Commit(Unit.Value);
        });
        return group;
    }

    public static async Task<Redis> InsertRedisAsync(this RedisTestStore store, Redis redis)
    {
        await store.RedisServices.InsertOneAsync(redis);
        return redis;
    }

    public static Task<long> CountOutboxMessagesAsync(this RedisTestStore store, string eventType) =>
        store.OutboxMessages.CountDocumentsAsync(message => message.EventType == eventType);
}
