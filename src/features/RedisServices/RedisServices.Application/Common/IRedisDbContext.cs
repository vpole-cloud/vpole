using MongoDB.Driver;
using Pole.Database;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Common;

public interface IRedisDbContext : IEventDbContext
{
    IMongoCollection<Redis> RedisServices { get; }
}

