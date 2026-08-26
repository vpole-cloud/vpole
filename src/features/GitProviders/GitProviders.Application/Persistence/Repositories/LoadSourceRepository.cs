using MongoDB.Driver;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence;

public delegate Task<SourceRepository?> LoadSourceRepository(
    string tenantId,
    string sourceRepositoryId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSourceRepository(IMongoDatabase database)
{
    public async Task<SourceRepository?> InvokeAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default) =>
        await database
            .GetCollection<SourceRepository>("SourceRepositories")
            .Find(GitProviderFilters.SourceRepositoryByTenantAndId(tenantId, sourceRepositoryId))
            .FirstOrDefaultAsync(cancellationToken);
}
