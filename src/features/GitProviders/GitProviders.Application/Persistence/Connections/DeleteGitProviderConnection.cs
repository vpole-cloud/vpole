using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
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

internal sealed class MongoDeleteGitProviderConnection(
    IMongoDatabase database,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox)
{
    public async Task<PersistDeletedGitProviderConnectionOutcome> InvokeAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        var connections = database.GetCollection<GitProviderConnection>("GitProviderConnections");
        var connectionFilter = GitProviderFilters.ConnectionByTenantAndId(tenantId, connectionId);
        return await transactions.InvokeOutcomeAsync<PersistDeletedGitProviderConnectionOutcome>(async (tx, ct) =>
            {
                if (!await connections.Find(tx.Session, connectionFilter).AnyAsync(ct))
                    return tx.Abort(new GitProviderConnectionNotFound(connectionId));

                var repositories = database.GetCollection<SourceRepository>("SourceRepositories");
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
