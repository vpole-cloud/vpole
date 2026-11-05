using Pole.Features.RedisServices.Application.Mongo;
using Pole.Features.RedisServices.Application.Declarations;
using Pole.Features.RedisServices.Application;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.RedisServices.Application.Services;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.RedisServices.Application.Common;
using MongoDB.Driver;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Lifecycle;

public sealed record RedisDeletionConflict(string RedisId);

public sealed record RedisDeletionStarted;

public sealed record RedisSecretBindingRemovalRequired(string RedisId);

public sealed record RedisDeletion(
    Redis Current,
    Redis Replacement,
    GitLifecycleAuthority? Authority = null,
    IReadOnlyList<ResourceConnectionPlanningVersion>? ReviewedConnections = null);

public union BeginRedisDeletionOutcome(
    RedisDeletionStarted,
    RedisSecretBindingRemovalRequired,
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
    IGitOpsMutations git,
    EnsureConsumerSecretBindingRemoval ensureBindingsRemovable)
{
    public async Task<BeginRedisDeletionOutcome> InvokeAsync(
        RedisDeletion request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var current = request.Current;
        var replacement = request.Replacement;
        RedisPersistenceGuards.EnsureTransaction(
            session,
            "Redis deletion start");
        RedisPersistenceGuards.EnsureSameIdentity(current, replacement);
        if (request.Authority is not null && request.ReviewedConnections is null)
            return new RedisDeletionConflict(current.Id);

        if (!await git.AuthorizeAsync(new(current.TenantId, DeclarativeRedisProvider.ResourceKind,
                current.Id, Lifecycle: request.Authority,
                LifecycleEffect: request.Authority is null ? null : GitLifecycleMutationEffect.Remove),
                session, cancellationToken))
            return new RedisGitOwnedConfiguration(current.Id);

        RedisPersistenceGuards.EnsureDeletionTransition(current, replacement);
        var bindings = await ensureBindingsRemovable(new(current.TenantId, RedisResourceDeployment.ResourceType,
            current.Id), session, cancellationToken);
        if (bindings is ConsumerSecretBindingsRequireRemoval)
            return new RedisSecretBindingRemovalRequired(current.Id);
        if (bindings is ConsumerSecretBindingDeletionChanged)
            return new RedisDeletionConflict(current.Id);

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
                ResourceId: current.Id,
                ReviewedConnections: request.ReviewedConnections),
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
            RedisFilters.ActiveAtPlanningVersion(current),
            replacement,
            cancellationToken: cancellationToken);
        return replace.ModifiedCount == 1
            ? new RedisDeletionStarted()
            : new RedisDeletionConflict(current.Id);
    }
}
