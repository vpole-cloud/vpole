using MongoDB.Driver;
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

internal sealed class MongoDeleteSourceRepository(IMongoDatabase database)
{
    public async Task<PersistDeletedSourceRepositoryOutcome> InvokeAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default)
    {
        var deleted = await database
            .GetCollection<SourceRepository>("SourceRepositories")
            .DeleteOneAsync(
                GitProviderFilters.SourceRepositoryByTenantAndId(tenantId, sourceRepositoryId),
                cancellationToken);
        return deleted.DeletedCount == 0
            ? new SourceRepositoryNotFound(sourceRepositoryId)
            : new SourceRepositoryDeleted();
    }
}
