using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Declarations;
using Pole.Features.RedisServices.Application.Mongo;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Services;

public sealed record RedisUpdateConflict(
    string RedisId
);

public sealed record RedisUpdated;

public sealed record RedisGitOwnedConfiguration(
    string RedisId
);

public sealed record RedisUpdate(
    Redis Current,
    Redis Replacement,
    GitMutationContext? Context
);

public union PersistUpdatedRedisOutcome(
    RedisUpdated,
    RedisUpdateConflict,
    RedisGitOwnedConfiguration);

public delegate Task<PersistUpdatedRedisOutcome> PersistUpdatedRedis(
    RedisUpdate request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedRedis(
    IMongoCollection<Redis> redisServices,
    StoreOutboxWrites storeOutboxWrites,
    IGitOpsMutations git
)
{
    public async Task<PersistUpdatedRedisOutcome> InvokeAsync(
        RedisUpdate request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        RedisPersistenceGuards.EnsureTransaction(session, "Redis update");
        var (current, replacement, context) = request;
        RedisPersistenceGuards.EnsureActiveUpdate(current, replacement);

        if (!await git.AuthorizeAsync(
                new AuthorizeGitResourceMutationRequest(
                    current.TenantId,
                    DeclarativeRedisProvider.ResourceKind,
                    current.Id,
                    context),
                session,
                cancellationToken))
            return new RedisGitOwnedConfiguration(current.Id);

        var replace = await redisServices.ReplaceOneAsync(
            session,
            RedisFilters.AtPlanningVersion(current),
            replacement,
            cancellationToken: cancellationToken);
        if (replace.ModifiedCount != 1)
            return new RedisUpdateConflict(current.Id);

        await storeOutboxWrites(session, [OutboxWrite.Create(current.ToUpdateEvent(replacement))], cancellationToken);
        return new RedisUpdated();
    }
}
