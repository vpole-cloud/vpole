using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence.Repositories;

public sealed record SourceRepositoryDeleted;

public union PersistDeletedSourceRepositoryOutcome(
    SourceRepositoryDeleted,
    SourceRepositoryNotFound);

public delegate Task<PersistDeletedSourceRepositoryOutcome> DeleteSourceRepository(
    string tenantId,
    string sourceRepositoryId,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteSourceRepository(IMongoDatabase database, MongoRunTransaction transactions, StoreOutboxWrites outbox)
{
    public async Task<PersistDeletedSourceRepositoryOutcome> InvokeAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default)
    {
        return await transactions.InvokeOutcomeAsync<PersistDeletedSourceRepositoryOutcome>(async (tx, ct) =>
        {
            var deleted = await database
                .GetCollection<SourceRepository>("SourceRepositories")
                .FindOneAndDeleteAsync(tx.Session,
                    GitProviderFilters.SourceRepositoryByTenantAndId(tenantId, sourceRepositoryId),
                    cancellationToken: ct);
            if (deleted is null)
                return tx.Abort(new SourceRepositoryNotFound(sourceRepositoryId));
            await outbox(tx.Session, [OutboxWrite.Create(deleted.ToChangedEvent())], ct);
            return tx.Commit(new SourceRepositoryDeleted());
        }, cancellationToken);
    }
}
