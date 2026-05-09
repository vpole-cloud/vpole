using MongoDB.Driver;
using Pole.Database;
using Pole.Feature.DnsRecords.Domain;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.RedisServices.Common;

public interface IRedisDbContext : IEventDbContext
{
    IMongoCollection<Redis> RedisServices { get; }
    IMongoCollection<ResourceGroup> ResourceGroups { get; }
    IMongoCollection<DnsRecord> DnsRecords { get; }
}