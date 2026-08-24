using MongoDB.Driver;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence;

public delegate Task<IReadOnlyList<GitProviderConnection>> LoadGitProviderConnections(
    string tenantId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadGitProviderConnections(IMongoDatabase database)
{
    public async Task<IReadOnlyList<GitProviderConnection>> InvokeAsync(
        string tenantId,
        CancellationToken cancellationToken = default) =>
        await database
            .GetCollection<GitProviderConnection>("GitProviderConnections")
            .Find(GitProviderFilters.ConnectionsByTenant(tenantId))
            .SortBy(item => item.Provider)
            .ThenBy(item => item.AccountLogin)
            .ToListAsync(cancellationToken);
}
