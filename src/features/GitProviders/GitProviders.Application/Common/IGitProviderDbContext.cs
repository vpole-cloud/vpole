using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Common;

public interface IGitProviderDbContext : IMongoDbContext
{
    IMongoCollection<GitProviderConnection> GitProviderConnections { get; }
    IMongoCollection<GitHubSetupAttempt> GitHubSetupAttempts { get; }
    IMongoCollection<SourceRepository> SourceRepositories { get; }
}
