using MongoDB.Driver;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence.Repositories;

public delegate Task<IReadOnlyList<SourceRepository>> LoadSourceRepositories(
    string tenantId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSourceRepositories(IMongoDatabase database)
{
    public async Task<IReadOnlyList<SourceRepository>> InvokeAsync(
        string tenantId,
        CancellationToken cancellationToken = default) =>
        await database
            .GetCollection<SourceRepository>("SourceRepositories")
            .Find(GitProviderFilters.SourceRepositoriesByTenant(tenantId))
            .SortBy(item => item.RepositoryFullName)
            .ThenBy(item => item.Branch)
            .ToListAsync(cancellationToken);
}
