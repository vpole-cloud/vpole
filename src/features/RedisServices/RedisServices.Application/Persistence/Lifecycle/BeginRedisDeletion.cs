using Pole.Features.GitOps.Contracts;
using Pole.Features.RedisServices.Application.Persistence.Mutations;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.RedisServices.Application.Common;
using MongoDB.Driver;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence.Lifecycle;

public sealed record RedisDeletionConflict(string RedisId);

public sealed record RedisDeletionStarted;

public sealed record RedisDeletion(
    Redis Current,
    Redis Replacement);

public union BeginRedisDeletionOutcome(
    RedisDeletionStarted,
    RedisDeletionConflict,
    RedisGitOwnedConfiguration);

public delegate Task<BeginRedisDeletionOutcome> BeginRedisDeletion(
    RedisDeletion request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

internal sealed class MongoBeginRedisDeletion(
    FenceRegisteredResource fenceRegisteredResource,
    CascadeConnectionsForResourceDeletion cascadeConnections,
    IMongoCollection<Redis> redisServices,
    IGitOpsMutations git)
{
    public async Task<BeginRedisDeletionOutcome> InvokeAsync(
        RedisDeletion request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var (current, replacement) = request;
        RedisPersistenceGuards.EnsureTransaction(
            session,
            "Redis deletion start");
        RedisPersistenceGuards.EnsureSameIdentity(current, replacement);

        if (!await git.AuthorizeAsync(new(current.TenantId, DeclarativeRedisProvider.ResourceKind,
                current.Id, null), session, cancellationToken))
            return new RedisGitOwnedConfiguration(current.Id);

        RedisPersistenceGuards.EnsureDeletionTransition(current, replacement);

        var registration = await fenceRegisteredResource(
            new FenceRegisteredResourceRequest(
                TenantId: current.TenantId,
                ResourceTypeId: RedisResourceDeployment.ResourceType,
                ResourceId: current.Id),
            session: session,
            cancellationToken: cancellationToken);
        if (registration is null)
            return new RedisDeletionConflict(current.Id);
        var cascade = await cascadeConnections(
            new CascadeConnectionsForResourceDeletionRequest(
                TenantId: current.TenantId,
                ResourceType: RedisResourceDeployment.ResourceType,
                ResourceId: current.Id),
            session: session,
            cancellationToken: cancellationToken);
        RedisDeletionConflict? cascadeConflict = cascade switch
        {
            ResourceConnectionsCascaded => null,
            ResourceConnectionCascadeConflict => new RedisDeletionConflict(current.Id)
        };
        if (cascadeConflict is not null)
            return cascadeConflict;

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
