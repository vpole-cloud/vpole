using MongoDB.Driver;
using Pole.Database;
using Pole.Feature.DnsRecords.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Common;

public interface IWebServiceDbContext : IEventDbContext
{
    IMongoCollection<WebService> WebServices { get; }
    IMongoCollection<ResourceGroup> ResourceGroups { get; }
    IMongoCollection<DnsRecord> DnsRecords { get; }
}