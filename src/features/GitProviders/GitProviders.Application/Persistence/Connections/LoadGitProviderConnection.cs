using MongoDB.Driver;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence.Connections;

public delegate Task<GitProviderConnection?> LoadGitProviderConnection(
    string tenantId,
    string connectionId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadGitProviderConnection(IMongoDatabase database)
{
    public async Task<GitProviderConnection?> InvokeAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken = default) =>
        await database
            .GetCollection<GitProviderConnection>("GitProviderConnections")
            .Find(GitProviderFilters.ConnectionByTenantAndId(tenantId, connectionId))
            .FirstOrDefaultAsync(cancellationToken);
}
