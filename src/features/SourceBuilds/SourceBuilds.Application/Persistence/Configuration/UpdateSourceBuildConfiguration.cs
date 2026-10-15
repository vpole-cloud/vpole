using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
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
    IMongoCollection<SourceBuildConfiguration> configurations,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox)
{
    public async Task<PersistUpdatedSourceBuildConfigurationOutcome>
        InvokeAsync(
            SourceBuildConfiguration current,
            SourceBuildRecipe recipe,
            DateTime updatedAtUtc,
            CancellationToken cancellationToken = default)
    {
        return await transactions.InvokeOutcomeAsync<PersistUpdatedSourceBuildConfigurationOutcome>(async (tx, ct) =>
            {
                var updated = await configurations.UpdateOneAsync(tx.Session,
                    SourceBuildConfigurationFilters.AtUpdatedAt(current),
                    Builders<SourceBuildConfiguration>.Update.Set(item => item.Recipe, recipe)
                        .Set(item => item.UpdatedAtUtc, updatedAtUtc),
                    cancellationToken: ct);
                var stored = await configurations
                    .Find(tx.Session, SourceBuildConfigurationFilters.ById(current.TenantId, current.Id))
                    .FirstOrDefaultAsync(ct);
                if (updated.MatchedCount == 1)
                {
                    if (updated.ModifiedCount == 1)
                        await outbox(tx.Session, [OutboxWrite.Create(stored!.ToChangedEvent())], ct);
                    return tx.Commit(stored!);
                }

                PersistUpdatedSourceBuildConfigurationOutcome rejected = stored is not null
                    ? new SourceBuildConfigurationUpdateConflict(current.Id)
                    : new SourceBuildConfigurationNotFound(current.Id);
                return tx.Abort(rejected);
            },
            cancellationToken);
    }
}
