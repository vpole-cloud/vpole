using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Common;

public interface IResourceGroupDbContext : IEventDbContext
{
    IMongoCollection<ResourceGroup> ResourceGroups { get; }
}