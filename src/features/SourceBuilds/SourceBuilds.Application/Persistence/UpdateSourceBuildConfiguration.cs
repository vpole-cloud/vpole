using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Persistence;

public union PersistUpdatedSourceBuildConfigurationError(
    SourceBuildConfigurationNotFound,
    SourceBuildConfigurationUpdateConflict);

public delegate Task<Result<SourceBuildConfiguration, PersistUpdatedSourceBuildConfigurationError>>
    UpdateSourceBuildConfiguration(
        SourceBuildConfiguration current,
        SourceBuildRecipe recipe,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken = default);

internal sealed class MongoUpdateSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations)
{
    public async Task<Result<SourceBuildConfiguration, PersistUpdatedSourceBuildConfigurationError>>
        InvokeAsync(
            SourceBuildConfiguration current,
            SourceBuildRecipe recipe,
            DateTime updatedAtUtc,
            CancellationToken cancellationToken = default)
    {
        var updated = await configurations.FindOneAndUpdateAsync(
            SourceBuildConfigurationFilters.AtUpdatedAt(current),
            Builders<SourceBuildConfiguration>.Update
                .Set(item => item.Recipe, recipe)
                .Set(item => item.UpdatedAtUtc, updatedAtUtc),
            new FindOneAndUpdateOptions<SourceBuildConfiguration>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);
        if (updated is not null)
            return new Ok<SourceBuildConfiguration>(updated);

        var exists = await configurations
            .Find(SourceBuildConfigurationFilters.ById(current.TenantId, current.Id))
            .AnyAsync(cancellationToken);
        return exists
            ? new Err<PersistUpdatedSourceBuildConfigurationError>(
                new(new SourceBuildConfigurationUpdateConflict(current.Id)))
            : new Err<PersistUpdatedSourceBuildConfigurationError>(
                new(new SourceBuildConfigurationNotFound(current.Id)));
    }
}
