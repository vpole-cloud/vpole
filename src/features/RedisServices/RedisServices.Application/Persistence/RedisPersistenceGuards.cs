using MongoDB.Driver;
using MongoDB.Bson;
using Pole.Features.RedisServices.Application.Persistence.Reads;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence;

internal static class RedisPersistenceGuards
{
    public static void EnsureIdentity(string tenantId, string redisId)
    {
        if (!ObjectId.TryParse(tenantId, out _))
            throw new ArgumentException("Redis tenant ID must be a MongoDB ObjectId.", nameof(tenantId));
        if (!ObjectId.TryParse(redisId, out _))
            throw new ArgumentException("Redis ID must be a MongoDB ObjectId.", nameof(redisId));
    }

    public static void EnsureCreated(Redis redis)
    {
        ArgumentNullException.ThrowIfNull(redis);
        EnsureIdentity(redis.TenantId, redis.Id);
        if (string.IsNullOrWhiteSpace(redis.Name))
            throw new ArgumentException("Redis name is required.", nameof(redis));
        if (!ObjectId.TryParse(redis.ResourceGroupId, out _))
        {
            throw new ArgumentException(
                "Redis resource group ID must be a MongoDB ObjectId.",
                nameof(redis));
        }
        ArgumentNullException.ThrowIfNull(redis.Spec);
        ArgumentNullException.ThrowIfNull(redis.Spec.Storage);
        if (redis.Spec.Storage.SizeGb <= 0)
            throw new ArgumentOutOfRangeException(nameof(redis), "Redis storage size must be greater than zero.");
        if (!ObjectId.TryParse(redis.Spec.Storage.StorageProfileId, out _))
        {
            throw new ArgumentException(
                "Redis storage profile ID must be a MongoDB ObjectId.",
                nameof(redis));
        }
    }

    public static void EnsureListQuery(RedisListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ObjectId.TryParse(query.TenantId, out _))
            throw new ArgumentException("Redis tenant ID must be a MongoDB ObjectId.", nameof(query));
        if (query.PageNumber <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Page number must be greater than zero.");
        if (query.PageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Page size must be greater than zero.");
        if (!string.IsNullOrWhiteSpace(query.ResourceGroupId) &&
            !ObjectId.TryParse(query.ResourceGroupId, out _))
        {
            throw new ArgumentException(
                "Redis resource group ID must be a MongoDB ObjectId.",
                nameof(query));
        }
    }

    public static void EnsureSameIdentity(
        Redis current,
        Redis replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(current.TenantId, replacement.TenantId, StringComparison.Ordinal) ||
            !string.Equals(current.Id, replacement.Id, StringComparison.Ordinal) ||
            !string.Equals(current.Name, replacement.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A Redis replacement must preserve tenant, resource identity, and immutable name.");
        }
    }

    public static void EnsureActiveUpdate(
        Redis current,
        Redis replacement)
    {
        EnsureSameIdentity(current, replacement);
        if (current.Lifecycle is not ActiveRedisLifecycle ||
            replacement.Lifecycle is not ActiveRedisLifecycle)
        {
            throw new InvalidOperationException(
                "Redis updates must replace an active Redis service with an active Redis service.");
        }
    }

    public static void EnsureDeletionTransition(
        Redis current,
        Redis replacement)
    {
        EnsureSameIdentity(current, replacement);
        if (current.Lifecycle is not ActiveRedisLifecycle ||
            replacement.Lifecycle is not DeletingRedisLifecycle)
        {
            throw new InvalidOperationException(
                "Redis deletion must transition an active Redis service to deleting.");
        }
    }

    public static void EnsureTransaction(
        IClientSessionHandle session,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                $"{operation} must run inside the logical resource transaction.");
        }
    }
}
