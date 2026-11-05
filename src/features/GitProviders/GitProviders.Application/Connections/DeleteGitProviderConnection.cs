using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Mongo;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.GitProviders.Application.Connections;

public sealed record GitProviderConnectionDeleted;

public union PersistDeletedGitProviderConnectionOutcome(
    GitProviderConnectionDeleted,
    GitProviderConnectionNotFound,
    GitProviderConnectionHasSourceRepositories);

public delegate Task<PersistDeletedGitProviderConnectionOutcome> DeleteGitProviderConnection(
    string tenantId,
    string connectionId,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteGitProviderConnection(
    IMongoCollection<GitProviderConnection> gitProviderConnections,
    IMongoCollection<SourceRepository> sourceRepositories,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox)
{
    public async Task<PersistDeletedGitProviderConnectionOutcome> InvokeAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var connections = gitProviderConnections;
        var connectionFilter = GitProviderFilters.ConnectionByTenantAndId(tenantId, connectionId);
        return await transactions.InvokeOutcomeAsync<PersistDeletedGitProviderConnectionOutcome>(async (tx, ct) =>
            {
                if (!await connections.Find(tx.Session, connectionFilter).AnyAsync(ct))
                    return tx.Abort(new GitProviderConnectionNotFound(connectionId));

                var repositories = sourceRepositories;
                if (await repositories
                        .Find(tx.Session,
                            GitProviderFilters.SourceRepositoriesByTenantAndConnection(tenantId, connectionId))
                        .AnyAsync(ct))
                {
                    return tx.Abort(new GitProviderConnectionHasSourceRepositories(connectionId));
                }

                var deleted =
                    await connections.FindOneAndDeleteAsync(tx.Session, connectionFilter, cancellationToken: ct);
                if (deleted is null)
                    return tx.Abort(new GitProviderConnectionNotFound(connectionId));
                await outbox(tx.Session, [OutboxWrite.Create(deleted.ToChangedEvent())], ct);
                return tx.Commit(new GitProviderConnectionDeleted());
            },
            cancellationToken);
    }
}
