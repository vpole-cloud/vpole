using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Common;

public interface IResourceGroupDbContext : IEventDbContext
{
    IMongoCollection<ResourceGroup> ResourceGroups { get; }
    IMongoCollection<IsolationScope> IsolationScopes { get; }
    IMongoCollection<RuntimeCell> RuntimeCells { get; }
}
