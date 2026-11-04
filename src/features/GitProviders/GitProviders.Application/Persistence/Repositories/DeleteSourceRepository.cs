using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Outbox.Contracts;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Contracts;

namespace Pole.Features.GitProviders.Application.Persistence.Repositories;

public sealed record SourceRepositoryDeleted;

public union PersistDeletedSourceRepositoryOutcome(
    SourceRepositoryDeleted,
    SourceRepositoryNotFound,
    SourceRepositoryHasBuildConfiguration,
    SourceRepositoryGitOwned);

public delegate Task<PersistDeletedSourceRepositoryOutcome> DeleteSourceRepository(
    string tenantId,
    string sourceRepositoryId,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteSourceRepository(
    IMongoCollection<SourceRepository> repositories,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox,
    EnsureNoSourceRepositoryBuildConfiguration ensureNoConfiguration,
    IGitOpsMutations git)
{
    public Task<PersistDeletedSourceRepositoryOutcome> InvokeAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<PersistDeletedSourceRepositoryOutcome>(async (tx, ct) =>
        {
            var filter = GitProviderFilters.SourceRepositoryByTenantAndId(tenantId, sourceRepositoryId);
            var repository = await repositories.FindOneAndUpdateAsync(tx.Session, filter,
                Builders<SourceRepository>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: ct);
            if (repository is null)
                return tx.Abort(new SourceRepositoryNotFound(sourceRepositoryId));

            if (!await git.AuthorizeAsync(new(tenantId, "SourceRepository", sourceRepositoryId), tx.Session, ct))
                return tx.Abort(new SourceRepositoryGitOwned(sourceRepositoryId));

            var guard = await ensureNoConfiguration(new(tenantId, sourceRepositoryId), tx.Session, ct);
            if (guard is SourceBuildSourceRepositoryConfigurationExists)
                return tx.Abort(new SourceRepositoryHasBuildConfiguration(sourceRepositoryId));

            await repositories.DeleteOneAsync(tx.Session, filter, cancellationToken: ct);
            await outbox(tx.Session, [OutboxWrite.Create(repository.ToChangedEvent())], ct);
            return tx.Commit(new SourceRepositoryDeleted());
        }, cancellationToken);
}
