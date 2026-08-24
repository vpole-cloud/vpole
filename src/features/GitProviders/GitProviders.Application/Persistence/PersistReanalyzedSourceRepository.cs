using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Persistence;

public delegate Task<Result<SourceRepository, SourceRepositoryNotFound>> PersistReanalyzedSourceRepository(
    SourceRepository repository,
    ProviderRepository providerRepository,
    SourceRepositoryAnalysis analysis,
    SourceBuildPlan selectedPlan,
    DateTime updatedAtUtc,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistReanalyzedSourceRepository(IMongoDatabase database)
{
    public async Task<Result<SourceRepository, SourceRepositoryNotFound>> InvokeAsync(
        SourceRepository repository,
        ProviderRepository providerRepository,
        SourceRepositoryAnalysis analysis,
        SourceBuildPlan selectedPlan,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var updated = await database
            .GetCollection<SourceRepository>("SourceRepositories")
            .FindOneAndUpdateAsync(
                GitProviderFilters.SourceRepositoryByTenantAndId(repository.TenantId, repository.Id),
                Builders<SourceRepository>.Update
                    .Set(item => item.RepositoryFullName, providerRepository.FullName)
                    .Set(item => item.RepositoryUrl, providerRepository.HtmlUrl)
                    .Set(item => item.IsPrivate, providerRepository.IsPrivate)
                    .Set(item => item.SelectedPlan, selectedPlan)
                    .Set(item => item.CommitSha, analysis.CommitSha)
                    .Set(item => item.AnalyzedAtUtc, analysis.AnalyzedAtUtc)
                    .Set(item => item.UpdatedAtUtc, updatedAtUtc),
                new FindOneAndUpdateOptions<SourceRepository>
                {
                    ReturnDocument = ReturnDocument.After
                },
                cancellationToken);
        return updated is null
            ? new Err<SourceRepositoryNotFound>(new SourceRepositoryNotFound(repository.Id))
            : new Ok<SourceRepository>(updated);
    }
}
