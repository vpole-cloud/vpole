using MongoDB.Driver;
using Pole.Database;
using Pole.Feature.DnsRecords.Domain;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Feature.DnsRecords.Core.Common;

public interface IDnsRecordDbContext : IEventDbContext
{
    IMongoCollection<ResourceGroup> ResourceGroups { get; }
    IMongoCollection<DnsRecord> DnsRecords { get; }
}