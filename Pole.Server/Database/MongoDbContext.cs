using MongoDB.Driver;
using Pole.Server.Models;

namespace Pole.Server.Database;

public class MongoDbContext
{
    public required IMongoDatabase Database { get; init; }
    public required IMongoClient Client { get; init; }
    public required IMongoCollection<WebService> WebServices { get; init; }
    public required IMongoCollection<ResourceGroup> ResourceGroups { get; init; }
}