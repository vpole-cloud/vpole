using MongoDB.Driver;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

internal sealed class MongoEnsureNoSourceRepositoryBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations)
{
    public async Task<Result<Unit, SourceBuildSourceRepositoryConfigurationExists>> InvokeAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default)
    {
        var hasConfiguration = await configurations
            .Find(SourceBuildConfigurationFilters.BySourceRepository(
                tenantId,
                sourceRepositoryId))
            .AnyAsync(cancellationToken);
        return hasConfiguration
            ? new Err<SourceBuildSourceRepositoryConfigurationExists>(
                new SourceBuildSourceRepositoryConfigurationExists(sourceRepositoryId))
            : new Ok<Unit>(Unit.Value);
    }
}
