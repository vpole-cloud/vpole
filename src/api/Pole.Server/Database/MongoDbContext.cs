using MongoDB.Driver;
using Pole.Features.DnsRecords.Application.Common;
using Pole.Features.DnsRecords.Domain;
using Pole.Features.Postgres.Application.Common;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Search.Application.Common;
using Pole.Features.Search.Domain;
using Pole.Features.Shared.Models;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Application.Common;
using Pole.Features.Users.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using PostgresCluster = Pole.Features.Postgres.Domain.PostgresCluster;

namespace Pole.Server.Database;

public class MongoDbContext(
    IMongoClient client,
    IMongoDatabase database
    ) : 
    IResourceGroupDbContext,
    IWebServiceDbContext,
    IRedisDbContext,
    ISearchDbContext,
    IPostgresDbContext,
    IDnsRecordDbContext,
    ITenantDbContext,
    IUserDbContext
{
    public IMongoDatabase Database { get; } = database;
    public IMongoClient Client { get; } = client;
    public IMongoCollection<WebService> WebServices { get; } = database.GetCollection<WebService>("WebServices");
    public IMongoCollection<WebServiceRelease> WebServiceReleases { get; } = database.GetCollection<WebServiceRelease>("WebServiceReleases");
    public IMongoCollection<Redis> RedisServices { get; } = database.GetCollection<Redis>("RedisServices");
    public IMongoCollection<SearchDocument> SearchDocuments { get; } = database.GetCollection<SearchDocument>("SearchDocuments");
    public IMongoCollection<ResourceGroup> ResourceGroups { get; } = database.GetCollection<ResourceGroup>("ResourceGroups");
    public IMongoCollection<DnsRecord> DnsRecords { get; } = database.GetCollection<DnsRecord>("DnsRecords");
    public IMongoCollection<PostgresCluster>  Postgres { get; } = database.GetCollection<PostgresCluster>("Postgres");
    public IMongoCollection<OutboxMessage> OutboxMessages { get; } = database.GetCollection<OutboxMessage>("OutboxMessages");
    public IMongoCollection<Tenant> Tenants { get; } = database.GetCollection<Tenant>("Tenants");
    public IMongoCollection<PoleUser> Users { get; } = database.GetCollection<PoleUser>("Users");
}

