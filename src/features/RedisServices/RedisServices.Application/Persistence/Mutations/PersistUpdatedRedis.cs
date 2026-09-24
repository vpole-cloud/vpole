using MongoDB.Driver;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence.Mutations;

public sealed record RedisUpdateConflict(string RedisId);

public sealed record RedisUpdated;

public union PersistUpdatedRedisOutcome(
    RedisUpdated,
    RedisUpdateConflict);

public delegate Task<PersistUpdatedRedisOutcome> PersistUpdatedRedis(
    IClientSessionHandle session,
    Redis current,
    Redis replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedRedis(
    IMongoCollection<Redis> redisServices,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<PersistUpdatedRedisOutcome> InvokeAsync(
        IClientSessionHandle session,
        Redis current,
        Redis replacement,
        CancellationToken cancellationToken = default)
    {
        RedisPersistenceGuards.EnsureTransaction(session, "Redis update");
        RedisPersistenceGuards.EnsureActiveUpdate(current, replacement);

        var replace = await redisServices.ReplaceOneAsync(
            session,
            RedisFilters.ActiveAtRevision(current),
            replacement,
            cancellationToken: cancellationToken);
        if (replace.ModifiedCount != 1)
        {
            return new RedisUpdateConflict(current.Id);
        }

        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(current.ToUpdateEvent(replacement))],
            cancellationToken);
        return new RedisUpdated();
    }
}
