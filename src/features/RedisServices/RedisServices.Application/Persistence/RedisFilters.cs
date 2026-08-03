using MongoDB.Driver;
using Pole.Database;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence;

internal static class RedisFilters
{
    public static FilterDefinition<Redis> VisibleToTenant(string tenantId) =>
        Builders<Redis>.Filter.ForTenant(tenantId);

    public static FilterDefinition<Redis> ById(
        string tenantId,
        string redisId) =>
        Builders<Redis>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<Redis>.Filter.Eq(redis => redis.Id, redisId));

    public static FilterDefinition<Redis> AtRevision(Redis redis)
    {
        ArgumentNullException.ThrowIfNull(redis);
        return Builders<Redis>.Filter.And(
            ById(redis.TenantId, redis.Id),
            Builders<Redis>.Filter.Eq(candidate => candidate.Revision, redis.Revision));
    }

    public static FilterDefinition<Redis> InLifecycle<TLifecycle>(
        string tenantId,
        string redisId)
        where TLifecycle : RedisLifecycle =>
        Builders<Redis>.Filter.And(
            ById(tenantId, redisId),
            Builders<Redis>.Filter.OfType<RedisLifecycle, TLifecycle>(redis => redis.Lifecycle));
}
