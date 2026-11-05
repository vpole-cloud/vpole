using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Mongo;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Repositories;

public delegate Task<SourceRepository?> LoadSourceRepository(
    string tenantId,
    string sourceRepositoryId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSourceRepository(IMongoCollection<SourceRepository> sourceRepositories)
{
    public async Task<SourceRepository?> InvokeAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default) =>
        await sourceRepositories
            .Find(GitProviderFilters.SourceRepositoryByTenantAndId(tenantId, sourceRepositoryId))
            .FirstOrDefaultAsync(cancellationToken);
}
