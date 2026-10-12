using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence.Repositories;

public union PersistReanalyzedSourceRepositoryOutcome(
    SourceRepository,
    SourceRepositoryNotFound);

public delegate Task<PersistReanalyzedSourceRepositoryOutcome> PersistReanalyzedSourceRepository(
    SourceRepository repository,
    ProviderRepository providerRepository,
    SourceRepositoryAnalysis analysis,
    SourceBuildPlan selectedPlan,
    DateTime updatedAtUtc,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistReanalyzedSourceRepository(IMongoDatabase database, MongoRunTransaction transactions, StoreOutboxWrites outbox)
{
    public async Task<PersistReanalyzedSourceRepositoryOutcome> InvokeAsync(
        SourceRepository repository,
        ProviderRepository providerRepository,
        SourceRepositoryAnalysis analysis,
        SourceBuildPlan selectedPlan,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        return await transactions.InvokeOutcomeAsync<PersistReanalyzedSourceRepositoryOutcome>(async (tx, ct) =>
        {
            var repositories = database.GetCollection<SourceRepository>("SourceRepositories");
            var filter = GitProviderFilters.SourceRepositoryByTenantAndId(repository.TenantId, repository.Id);
            var updated = await repositories.UpdateOneAsync(tx.Session, filter,
                    Builders<SourceRepository>.Update
                        .Set(item => item.RepositoryFullName, providerRepository.FullName)
                        .Set(item => item.RepositoryUrl, providerRepository.HtmlUrl)
                        .Set(item => item.IsPrivate, providerRepository.IsPrivate)
                        .Set(item => item.SelectedPlan, selectedPlan)
                        .Set(item => item.CommitSha, analysis.CommitSha)
                        .Set(item => item.AnalyzedAtUtc, analysis.AnalyzedAtUtc)
                        .Set(item => item.UpdatedAtUtc, updatedAtUtc), cancellationToken: ct);
            if (updated.MatchedCount == 0)
                return tx.Abort(new SourceRepositoryNotFound(repository.Id));
            var stored = await repositories.Find(tx.Session, filter).FirstAsync(ct);
            if (updated.ModifiedCount == 1)
                await outbox(tx.Session, [OutboxWrite.Create(stored.ToChangedEvent())], ct);
            return tx.Commit(stored);
        }, cancellationToken);
    }
}
