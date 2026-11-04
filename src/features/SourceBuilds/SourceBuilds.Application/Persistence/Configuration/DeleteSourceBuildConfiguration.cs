using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Outbox.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Contracts;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

public sealed record SourceBuildConfigurationRemovalRecorded;

public union PersistDeletedSourceBuildConfigurationOutcome(
    SourceBuildConfigurationRemovalRecorded,
    SourceBuildConfigurationNotFound,
    SourceBuildConfigurationHasActiveJobs,
    SourceBuildConfigurationInUse,
    SourceBuildConfigurationGitOwned);

public delegate Task<PersistDeletedSourceBuildConfigurationOutcome>
    DeleteSourceBuildConfiguration(
        string tenantId,
        string configurationId,
        CancellationToken cancellationToken = default);

internal sealed class MongoDeleteSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations,
    IMongoCollection<SourceBuildJob> jobs,
    MongoRunTransaction transactions,
    IGitOpsMutations git,
    LoadSourceBuildTarget loadTarget,
    StoreOutboxWrites outbox)
{
    public Task<PersistDeletedSourceBuildConfigurationOutcome> InvokeAsync(
        string tenantId,
        string configurationId,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<PersistDeletedSourceBuildConfigurationOutcome>(async (transaction, ct) =>
            {
                if (!await git.AuthorizeAsync(new(tenantId, "SourceBuildConfiguration", configurationId),
                    transaction.Session, ct))
                    return transaction.Abort(new SourceBuildConfigurationGitOwned(configurationId));

                // Queue and promotion write this same root before inspecting related documents.
                var fenced = await configurations.UpdateOneAsync(transaction.Session,
                    SourceBuildConfigurationFilters.ById(tenantId, configurationId),
                    Builders<SourceBuildConfiguration>.Update.Inc(item => item.ReferenceFence, 1),
                    cancellationToken: ct);
                if (fenced.MatchedCount != 1)
                    return transaction.Abort(new SourceBuildConfigurationNotFound(configurationId));

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

                var current = await configurations.Find(transaction.Session,
                    SourceBuildConfigurationFilters.ById(tenantId, configurationId)).SingleAsync(ct);
                var target = await loadTarget(new(tenantId, current.WebServiceId), transaction.Session, ct);
                if (target is SourceBuildTargetSnapshot bound && bound.ConfigurationId == configurationId)
                    return transaction.Abort(new SourceBuildConfigurationInUse(configurationId, current.WebServiceId));

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
