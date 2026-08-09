using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Common;

public interface IResourceDeploymentDbContext : IEventDbContext
{
    IMongoCollection<ResourceDeployment> ResourceDeployments { get; }
}
