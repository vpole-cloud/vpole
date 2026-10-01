using MongoDB.Driver;
using MongoDB.Bson;
using Pole.Database;
using Pole.Features.RedisServices.Application.Persistence.Reads;
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

    public static FilterDefinition<Redis> ByName(
        string tenantId,
        string name) =>
        Builders<Redis>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<Redis>.Filter.Eq(redis => redis.Name, name));

    public static FilterDefinition<Redis> AtRevision(Redis redis)
    {
        ArgumentNullException.ThrowIfNull(redis);
        return Builders<Redis>.Filter.And(
            ById(redis.TenantId, redis.Id),
            Builders<Redis>.Filter.Eq(candidate => candidate.Revision, redis.Revision));
    }

    public static FilterDefinition<Redis> ActiveAtRevision(Redis redis) =>
        Builders<Redis>.Filter.And(
            AtRevision(redis),
            Builders<Redis>.Filter.OfType<RedisLifecycle, ActiveRedisLifecycle>(
                candidate => candidate.Lifecycle));

    public static FilterDefinition<Redis> InLifecycle<TLifecycle>(
        string tenantId,
        string redisId)
        where TLifecycle : RedisLifecycle =>
        Builders<Redis>.Filter.And(
            ById(tenantId, redisId),
            Builders<Redis>.Filter.OfType<RedisLifecycle, TLifecycle>(redis => redis.Lifecycle));

    public static FilterDefinition<Redis> ForList(RedisListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var builder = Builders<Redis>.Filter;
        var filters = new List<FilterDefinition<Redis>>
        {
            VisibleToTenant(query.TenantId)
        };
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            filters.Add(builder.Regex(
                redis => redis.Name,
                new BsonRegularExpression(
                    System.Text.RegularExpressions.Regex.Escape(query.Search),
                    "i")));
        }
        if (!string.IsNullOrWhiteSpace(query.ResourceGroupId))
        {
            filters.Add(builder.Eq(
                redis => redis.ResourceGroupId,
                query.ResourceGroupId));
        }

        return filters.Count == 1
            ? filters[0]
            : builder.And(filters);
    }
}
