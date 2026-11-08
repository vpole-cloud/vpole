using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Testing;

namespace Pole.Features.RedisServices.Tests.Integration.Infrastructure;

public static class RedisTestSeedExtensions
{
    public static async Task<ResourceGroup> InsertResourceGroupAsync(this RedisTestStore store, ResourceGroup group)
    {
        await new ResourceGroupTestData(store.Database).SeedAsync(group);
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
