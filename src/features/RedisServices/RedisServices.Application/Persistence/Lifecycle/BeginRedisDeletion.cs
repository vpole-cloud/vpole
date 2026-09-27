using MongoDB.Driver;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence.Lifecycle;

public sealed record RedisDeletionConflict(string RedisId);

public sealed record RedisDeletionStarted;

public union BeginRedisDeletionOutcome(
    RedisDeletionStarted,
    RedisDeletionConflict);

public delegate Task<BeginRedisDeletionOutcome> BeginRedisDeletion(
    IClientSessionHandle session,
    Redis current,
    Redis replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoBeginRedisDeletion(
    IMongoCollection<Redis> redisServices)
{
    public async Task<BeginRedisDeletionOutcome> InvokeAsync(
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
            ? new RedisDeletionStarted()
            : new RedisDeletionConflict(current.Id);
    }
}
