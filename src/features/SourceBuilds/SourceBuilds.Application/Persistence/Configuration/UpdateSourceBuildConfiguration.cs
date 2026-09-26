using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

public union PersistUpdatedSourceBuildConfigurationOutcome(
    SourceBuildConfiguration,
    SourceBuildConfigurationNotFound,
    SourceBuildConfigurationUpdateConflict);

public delegate Task<PersistUpdatedSourceBuildConfigurationOutcome>
    UpdateSourceBuildConfiguration(
        SourceBuildConfiguration current,
        SourceBuildRecipe recipe,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken = default);

internal sealed class MongoUpdateSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations)
{
    public async Task<PersistUpdatedSourceBuildConfigurationOutcome>
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
            return updated;

        var exists = await configurations
            .Find(SourceBuildConfigurationFilters.ById(current.TenantId, current.Id))
            .AnyAsync(cancellationToken);
        return exists
            ? new SourceBuildConfigurationUpdateConflict(current.Id)
            : new SourceBuildConfigurationNotFound(current.Id);
    }
}
