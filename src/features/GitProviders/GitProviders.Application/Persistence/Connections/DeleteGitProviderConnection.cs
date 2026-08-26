using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Persistence;

public union DeleteGitProviderConnectionError(
    GitProviderConnectionNotFound,
    GitProviderConnectionHasSourceRepositories);

public delegate Task<Result<Unit, DeleteGitProviderConnectionError>> DeleteGitProviderConnection(
    string tenantId,
    string connectionId,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteGitProviderConnection(IMongoDatabase database)
{
    public async Task<Result<Unit, DeleteGitProviderConnectionError>> InvokeAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var connections = database.GetCollection<GitProviderConnection>("GitProviderConnections");
        var connectionFilter = GitProviderFilters.ConnectionByTenantAndId(tenantId, connectionId);
        if (!await connections.Find(connectionFilter).AnyAsync(cancellationToken))
        {
            return new Err<DeleteGitProviderConnectionError>(
                new(new GitProviderConnectionNotFound(connectionId)));
        }

        var repositories = database.GetCollection<SourceRepository>("SourceRepositories");
        if (await repositories
                .Find(GitProviderFilters.SourceRepositoriesByTenantAndConnection(tenantId, connectionId))
                .AnyAsync(cancellationToken))
        {
            return new Err<DeleteGitProviderConnectionError>(
                new(new GitProviderConnectionHasSourceRepositories(connectionId)));
        }

        var deleted = await connections.DeleteOneAsync(connectionFilter, cancellationToken);
        return deleted.DeletedCount == 0
            ? new Err<DeleteGitProviderConnectionError>(
                new(new GitProviderConnectionNotFound(connectionId)))
            : new Ok<Unit>(Unit.Value);
    }
}
