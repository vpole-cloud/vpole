using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

public sealed record SourceBuildConfigurationRemovalRecorded;

public union PersistDeletedSourceBuildConfigurationOutcome(
    SourceBuildConfigurationRemovalRecorded,
    SourceBuildConfigurationNotFound,
    SourceBuildConfigurationHasActiveJobs);

public delegate Task<PersistDeletedSourceBuildConfigurationOutcome>
    DeleteSourceBuildConfiguration(
        string tenantId,
        string configurationId,
        CancellationToken cancellationToken = default);

internal sealed class MongoDeleteSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations,
    IMongoCollection<SourceBuildJob> jobs,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox)
{
    public Task<PersistDeletedSourceBuildConfigurationOutcome> InvokeAsync(
        string tenantId,
        string configurationId,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<PersistDeletedSourceBuildConfigurationOutcome>(async (transaction, ct) =>
            {
                var hasActiveJobs = await jobs
                    .Find(transaction.Session,
                        SourceBuildJobFilters.ActiveForConfiguration(tenantId, configurationId))
                    .AnyAsync(ct);
                if (hasActiveJobs)
                {
                    PersistDeletedSourceBuildConfigurationOutcome rejected =
                        new SourceBuildConfigurationHasActiveJobs(configurationId);
                    return transaction.Abort(rejected);
                }

                var deleted = await configurations.FindOneAndDeleteAsync(
                    transaction.Session,
                    SourceBuildConfigurationFilters.ById(tenantId, configurationId),
                    cancellationToken: ct);
                if (deleted is not null)
                    await outbox(transaction.Session, [OutboxWrite.Create(deleted.ToChangedEvent())], ct);
                PersistDeletedSourceBuildConfigurationOutcome outcome = deleted is not null
                    ? new SourceBuildConfigurationRemovalRecorded()
                    : new SourceBuildConfigurationNotFound(configurationId);
                return deleted is not null
                    ? transaction.Commit(outcome)
                    : transaction.Abort(outcome);
            },
            cancellationToken);
}
