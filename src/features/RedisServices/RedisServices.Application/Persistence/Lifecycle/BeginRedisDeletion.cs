using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.RedisServices.Application.Common;
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
    FenceRegisteredResource fenceRegisteredResource,
    CascadeConnectionsForResourceDeletion cascadeConnections,
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
