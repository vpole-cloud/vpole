using MongoDB.Driver;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence.Mutations;

public sealed record RedisServiceNameAlreadyExists(string Name);

public delegate Task<bool> PersistCreatedRedis(
    IClientSessionHandle session,
    Redis redis,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedRedis(
    IMongoCollection<Redis> redisServices,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<bool> InvokeAsync(
        IClientSessionHandle session,
        Redis redis,
        CancellationToken cancellationToken = default)
    {
        RedisPersistenceGuards.EnsureTransaction(session, "Redis creation");
        RedisPersistenceGuards.EnsureCreated(redis);

        try
        {
            await redisServices.InsertOneAsync(
                session,
                redis,
                cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(redis.ToCreateEvent())],
            cancellationToken);
        return true;
    }
}
