using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence.Connections;

public sealed record GitProviderConnectionDeleted;

public union PersistDeletedGitProviderConnectionOutcome(
    GitProviderConnectionDeleted,
    GitProviderConnectionNotFound,
    GitProviderConnectionHasSourceRepositories);

public delegate Task<PersistDeletedGitProviderConnectionOutcome> DeleteGitProviderConnection(
    string tenantId,
    string connectionId,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteGitProviderConnection(IMongoDatabase database)
{
    public async Task<PersistDeletedGitProviderConnectionOutcome> InvokeAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var connections = database.GetCollection<GitProviderConnection>("GitProviderConnections");
        var connectionFilter = GitProviderFilters.ConnectionByTenantAndId(tenantId, connectionId);
        if (!await connections.Find(connectionFilter).AnyAsync(cancellationToken))
        {
            return new GitProviderConnectionNotFound(connectionId);
        }

        var repositories = database.GetCollection<SourceRepository>("SourceRepositories");
        if (await repositories
                .Find(GitProviderFilters.SourceRepositoriesByTenantAndConnection(tenantId, connectionId))
                .AnyAsync(cancellationToken))
        {
            return new GitProviderConnectionHasSourceRepositories(connectionId);
        }

        var deleted = await connections.DeleteOneAsync(connectionFilter, cancellationToken);
        return deleted.DeletedCount == 0
            ? new GitProviderConnectionNotFound(connectionId)
            : new GitProviderConnectionDeleted();
    }
}
