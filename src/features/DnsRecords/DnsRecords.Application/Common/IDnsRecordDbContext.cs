using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DnsRecords.Domain;

namespace Pole.Features.DnsRecords.Application.Common;

public interface IDnsRecordDbContext : IEventDbContext
{
    IMongoCollection<DnsRecord> DnsRecords { get; }
}

