using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceDeployments.Application.Common;

public interface IResourceDeploymentDbContext : IEventDbContext
{
    IMongoCollection<ResourceDeployment> ResourceDeployments { get; }
    IMongoCollection<RuntimeCell> RuntimeCells { get; }
}
