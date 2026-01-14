using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Server.Models;

namespace Pole.Server.Database;

public class MongoDbContext
{
    [FromServices]
    public required IMongoDatabase Database { get; init; }
    [FromServices]
    public required IMongoClient Client { get; init; }
    [FromServices]
    public required IMongoCollection<WebService> WebServices { get; init; }
    [FromServices]
    public required IMongoCollection<ResourceGroup> ResourceGroups { get; init; }
}