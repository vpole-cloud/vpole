using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Pole.Server.Features.ResourceGroups.Common;
using Pole.Server.Features.WebServices.Common;

namespace Pole.Server.Database;

public class MongoDbContext(
    IMongoClient client,
    IMongoDatabase database)
{
    public IMongoDatabase Database { get; } = database;
    public IMongoClient Client { get; } = client;
    public IMongoCollection<WebService> WebServices { get; } = database.GetCollection<WebService>("WebServices");
    public IMongoCollection<ResourceGroup> ResourceGroups { get; } = database.GetCollection<ResourceGroup>("ResourceGroups");
}