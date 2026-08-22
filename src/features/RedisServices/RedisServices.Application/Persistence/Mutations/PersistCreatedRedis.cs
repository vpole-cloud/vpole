using MongoDB.Driver;
using Pole.Database;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence;

public delegate Task PersistCreatedRedis(
    IClientSessionHandle session,
    Redis redis,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedRedis(
    IMongoCollection<Redis> redisServices,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task InvokeAsync(
        IClientSessionHandle session,
        Redis redis,
        CancellationToken cancellationToken = default)
    {
        RedisPersistenceGuards.EnsureTransaction(session, "Redis creation");
        RedisPersistenceGuards.EnsureCreated(redis);

        await redisServices.InsertOneAsync(
            session,
            redis,
            cancellationToken: cancellationToken);
        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(redis.ToCreateEvent())],
            cancellationToken);
    }
}
