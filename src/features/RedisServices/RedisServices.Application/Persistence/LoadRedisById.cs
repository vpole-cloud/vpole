using MongoDB.Driver;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Persistence;

public delegate Task<Redis?> LoadRedisById(
    string tenantId,
    string redisId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadRedisById(
    IMongoCollection<Redis> redisServices)
{
    public async Task<Redis?> InvokeAsync(
        string tenantId,
        string redisId,
        CancellationToken cancellationToken = default) =>
        await redisServices
            .Find(RedisFilters.ById(tenantId, redisId))
            .FirstOrDefaultAsync(cancellationToken);
}
