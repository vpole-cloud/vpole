using MongoDB.Driver;
using Pole.Features.RedisServices.Domain;

namespace Pole.Tests.Integration.Infrastructure;

internal static class RedisTestPersistence
{
    public static IMongoCollection<Redis> RedisServices(
        this IMongoDatabase database) =>
        database.GetCollection<Redis>("RedisServices");
}
