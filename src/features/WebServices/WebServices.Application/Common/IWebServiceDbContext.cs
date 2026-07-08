using MongoDB.Driver;
using Pole.Database;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Common;

public interface IWebServiceDbContext : IEventDbContext
{
    IMongoCollection<WebService> WebServices { get; }
    IMongoCollection<WebServiceRelease> WebServiceReleases { get; }
}

