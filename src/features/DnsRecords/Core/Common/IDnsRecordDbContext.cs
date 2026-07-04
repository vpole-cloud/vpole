using MongoDB.Driver;
using Pole.Database;
using Pole.Feature.DnsRecords.Domain;

namespace Pole.Feature.DnsRecords.Core.Common;

public interface IDnsRecordDbContext : IEventDbContext
{
    IMongoCollection<DnsRecord> DnsRecords { get; }
}
