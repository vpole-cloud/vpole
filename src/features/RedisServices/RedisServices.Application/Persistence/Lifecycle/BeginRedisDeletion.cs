using MongoDB.Driver;
using Pole.Features.RedisServices.Domain;
using Pole.ResultType;

namespace Pole.Features.RedisServices.Application.Persistence;

public sealed record RedisDeletionConflict(string RedisId);

public delegate Task<Result<Unit, RedisDeletionConflict>> BeginRedisDeletion(
    IClientSessionHandle session,
    Redis current,
    Redis replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoBeginRedisDeletion(
    IMongoCollection<Redis> redisServices)
{
    public async Task<Result<Unit, RedisDeletionConflict>> InvokeAsync(
        IClientSessionHandle session,
        Redis current,
        Redis replacement,
        CancellationToken cancellationToken = default)
    {
        RedisPersistenceGuards.EnsureTransaction(
            session,
            "Redis deletion start");
        RedisPersistenceGuards.EnsureDeletionTransition(current, replacement);

        var replace = await redisServices.ReplaceOneAsync(
            session,
            RedisFilters.ActiveAtRevision(current),
            replacement,
            cancellationToken: cancellationToken);
        return replace.ModifiedCount == 1
            ? new Ok<Unit>(Unit.Value)
            : new Err<RedisDeletionConflict>(new RedisDeletionConflict(current.Id));
    }
}
