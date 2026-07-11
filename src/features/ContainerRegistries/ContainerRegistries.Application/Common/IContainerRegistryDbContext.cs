using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Domain;

namespace Pole.Features.ContainerRegistries.Application.Common;

public interface IContainerRegistryDbContext
{
    IMongoCollection<RegistryProject> RegistryProjects { get; }
    IMongoCollection<RegistryCredential> RegistryCredentials { get; }
}
