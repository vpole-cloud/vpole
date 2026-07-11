using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Common;

public interface IContainerRegistryDbContext : IMongoDbContext
{
    IMongoCollection<RegistryProject> RegistryProjects { get; }
    IMongoCollection<RegistryCredential> RegistryCredentials { get; }
}
