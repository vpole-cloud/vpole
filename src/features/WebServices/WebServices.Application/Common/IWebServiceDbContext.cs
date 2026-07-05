using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Common;

public interface IWebServiceDbContext : IEventDbContext
{
    IMongoCollection<WebService> WebServices { get; }
    IMongoCollection<ResourceGroup> ResourceGroups { get; }
}

