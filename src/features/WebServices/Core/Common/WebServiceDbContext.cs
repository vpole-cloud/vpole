using MongoDB.Driver;
using Pole.Database;
using Pole.Feature.DnsRecords.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Shared.Models;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Common;

public class WebServiceDbContext(
    IMongoClient client,
    IMongoDatabase database
    ) : IWebServiceDbContext
{
    public IMongoDatabase Database { get; } = database;
    public IMongoClient Client { get; } = client;
    public IMongoCollection<WebService> WebServices { get; } = database.GetCollection<WebService>("WebServices");
    public IMongoCollection<ResourceGroup> ResourceGroups { get; } = database.GetCollection<ResourceGroup>("ResourceGroups");
    public IMongoCollection<DnsRecord> DnsRecords { get; } = database.GetCollection<DnsRecord>("DnsRecords");
    public IMongoCollection<OutboxMessage> OutboxMessages { get; } = database.GetCollection<OutboxMessage>("OutboxMessages");
}

public interface IWebServiceDbContext : IEventDbContext
{
    IMongoCollection<WebService> WebServices { get; }
    IMongoCollection<ResourceGroup> ResourceGroups { get; }
    IMongoCollection<DnsRecord> DnsRecords { get; }
}

/*
 *
   public IMongoCollection<Redis> RedisServices { get; } = database.GetCollection<Redis>("RedisServices");
   
   public IMongoCollection<PostgresCluster>  Postgres { get; } = database.GetCollection<PostgresCluster>("Postgres");
 */