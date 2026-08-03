using MongoDB.Driver;
using Pole.Features.RedisServices.Domain;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Persistence;

public delegate Task<Result> BeginRedisDeletion(
    IClientSessionHandle session,
    Redis current,
    Redis replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoBeginRedisDeletion(
    IMongoCollection<Redis> redisServices)
{
    public async Task<Result> InvokeAsync(
        IClientSessionHandle session,
        Redis current,
        Redis replacement,
        CancellationToken cancellationToken = default)
    {
        RedisPersistenceGuards.EnsureTransaction(
            session,
            "Redis deletion start");
        RedisPersistenceGuards.EnsureSameIdentity(current, replacement);

        var replace = await redisServices.ReplaceOneAsync(
            session,
            RedisFilters.AtRevision(current),
            replacement,
            cancellationToken: cancellationToken);
        return replace.ModifiedCount == 1
            ? Result.Success()
            : Failures.Conflict(
                "The Redis service changed while deletion was being accepted.");
    }
}
