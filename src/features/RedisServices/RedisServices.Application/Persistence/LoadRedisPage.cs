using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence;

public sealed record RedisListQuery(
    string TenantId,
    string? Search,
    string? ResourceGroupId,
    int PageNumber,
    int PageSize)
{
    public int Skip => checked((PageNumber - 1) * PageSize);
}

public sealed record RedisPage(
    IReadOnlyList<Redis> Items,
    long TotalCount);

public delegate Task<RedisPage> LoadRedisPage(
    RedisListQuery query,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadRedisPage(
    IMongoCollection<Redis> redisServices)
{
    public async Task<RedisPage> InvokeAsync(
        RedisListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var builder = Builders<Redis>.Filter;
        var filters = new List<FilterDefinition<Redis>>
        {
            RedisFilters.VisibleToTenant(query.TenantId)
        };
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            filters.Add(builder.Regex(
                redis => redis.Name,
                new BsonRegularExpression(
                    Regex.Escape(query.Search),
                    "i")));
        }
        if (!string.IsNullOrWhiteSpace(query.ResourceGroupId))
        {
            filters.Add(builder.Eq(
                redis => redis.ResourceGroupId,
                query.ResourceGroupId));
        }

        var filter = filters.Count == 1
            ? filters[0]
            : builder.And(filters);
        var totalCount = await redisServices.CountDocumentsAsync(
            filter,
            cancellationToken: cancellationToken);
        var items = await redisServices
            .Find(filter)
            .Skip(query.Skip)
            .Limit(query.PageSize)
            .ToListAsync(cancellationToken);
        return new RedisPage(items, totalCount);
    }
}
