using MongoDB.Driver;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence.Reads;

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
        RedisPersistenceGuards.EnsureListQuery(query);
        var filter = RedisFilters.ForList(query);
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
