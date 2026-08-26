using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Persistence;

public delegate Task<Result<Unit, SourceRepositoryNotFound>> DeleteSourceRepository(
    string tenantId,
    string sourceRepositoryId,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteSourceRepository(IMongoDatabase database)
{
    public async Task<Result<Unit, SourceRepositoryNotFound>> InvokeAsync(
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
            ? new Err<SourceRepositoryNotFound>(new SourceRepositoryNotFound(sourceRepositoryId))
            : new Ok<Unit>(Unit.Value);
    }
}
