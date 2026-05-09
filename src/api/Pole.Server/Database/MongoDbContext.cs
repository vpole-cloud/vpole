using MongoDB.Driver;
using Pole.Feature.DnsRecords.Domain;
using Pole.Features.Postgres.Domain;
using Pole.Features.RedisServices.Common;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceGroups.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Common;
using Pole.Features.WebServices.Domain;

namespace Pole.Server.Database;

public class MongoDbContext(
    IMongoClient client,
    IMongoDatabase database
    ) : IResourceGroupDbContext, IWebServiceDbContext, IRedisDbContext
{
    public IMongoDatabase Database { get; } = database;
    public IMongoClient Client { get; } = client;
    public IMongoCollection<WebService> WebServices { get; } = database.GetCollection<WebService>("WebServices");
    public IMongoCollection<Redis> RedisServices { get; } = database.GetCollection<Redis>("RedisServices");
    public IMongoCollection<ResourceGroup> ResourceGroups { get; } = database.GetCollection<ResourceGroup>("ResourceGroups");
    public IMongoCollection<DnsRecord> DnsRecords { get; } = database.GetCollection<DnsRecord>("DnsRecords");
    public IMongoCollection<PostgresCluster>  Postgres { get; } = database.GetCollection<PostgresCluster>("Postgres");
    public IMongoCollection<OutboxMessage> OutboxMessages { get; } = database.GetCollection<OutboxMessage>("OutboxMessages");
}