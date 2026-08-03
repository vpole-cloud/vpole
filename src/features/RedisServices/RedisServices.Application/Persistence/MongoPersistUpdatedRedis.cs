using MongoDB.Driver;
using Pole.Database;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Persistence;

public delegate Task<Result> PersistUpdatedRedis(
    IClientSessionHandle session,
    Redis current,
    Redis replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedRedis(
    IMongoCollection<Redis> redisServices,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<Result> InvokeAsync(
        IClientSessionHandle session,
        Redis current,
        Redis replacement,
        CancellationToken cancellationToken = default)
    {
        RedisPersistenceGuards.EnsureTransaction(session, "Redis update");
        RedisPersistenceGuards.EnsureSameIdentity(current, replacement);

        var replace = await redisServices.ReplaceOneAsync(
            session,
            RedisFilters.AtRevision(current),
            replacement,
            cancellationToken: cancellationToken);
        if (replace.ModifiedCount != 1)
        {
            return Failures.Conflict(
                "The Redis service changed while the update was being accepted.");
        }

        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(current.ToUpdateEvent(replacement))],
            cancellationToken);
        return Result.Success();
    }
}
