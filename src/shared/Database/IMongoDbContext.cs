using MongoDB.Driver;

namespace Pole.Database;

public interface IMongoDbContext
{
    IMongoClient Client { get; }
    IMongoDatabase Database { get; }
}