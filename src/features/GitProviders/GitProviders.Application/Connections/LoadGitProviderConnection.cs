using Pole.Features.GitProviders.Application.Mongo;
using MongoDB.Driver;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Connections;

public delegate Task<GitProviderConnection?> LoadGitProviderConnection(
    string tenantId,
    string connectionId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadGitProviderConnection(IMongoCollection<GitProviderConnection> gitProviderConnections)
{
    public async Task<GitProviderConnection?> InvokeAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken = default) =>
        await gitProviderConnections
            .Find(GitProviderFilters.ConnectionByTenantAndId(tenantId, connectionId))
            .FirstOrDefaultAsync(cancellationToken);
}
