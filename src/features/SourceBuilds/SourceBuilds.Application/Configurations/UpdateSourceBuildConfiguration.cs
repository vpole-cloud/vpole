using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Outbox.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Configurations;

public sealed record UpdateSourceBuildConfigurationCommand(
    SourceBuildConfiguration Current,
    SourceBuildRecipe Recipe,
    DateTime UpdatedAtUtc);

public union PersistUpdatedSourceBuildConfigurationOutcome(
    SourceBuildConfiguration,
    SourceBuildConfigurationNotFound,
    SourceBuildConfigurationUpdateConflict,
    SourceBuildConfigurationGitOwned);

public delegate Task<PersistUpdatedSourceBuildConfigurationOutcome> UpdateSourceBuildConfiguration(
    UpdateSourceBuildConfigurationCommand command,
    CancellationToken cancellationToken = default);

internal sealed class MongoUpdateSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations,
    MongoRunTransaction transactions,
    IGitOpsMutations git,
    StoreOutboxWrites outbox)
{
    public Task<PersistUpdatedSourceBuildConfigurationOutcome> InvokeAsync(
        UpdateSourceBuildConfigurationCommand command,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<PersistUpdatedSourceBuildConfigurationOutcome>(async (tx, ct) =>
        {
            var current = command.Current;
            if (!await git.AuthorizeAsync(
                    new(current.TenantId, "SourceBuildConfiguration", current.Id), tx.Session, ct))
                return tx.Abort(new SourceBuildConfigurationGitOwned(current.Id));

            var result = await UpdateAsync(command, tx.Session, ct);
            return result is SourceBuildConfiguration ? tx.Commit(result) : tx.Abort(result);
        }, cancellationToken);

    public async Task<PersistUpdatedSourceBuildConfigurationOutcome> UpdateAsync(
        UpdateSourceBuildConfigurationCommand command,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = command.Current;
        var update = Builders<SourceBuildConfiguration>.Update.Inc(item => item.ReferenceFence, 1);
        if (current.Recipe != command.Recipe)
        {
            update = update.Set(item => item.Recipe, command.Recipe)
                .Inc(item => item.Revision, 1).Set(item => item.UpdatedAtUtc, command.UpdatedAtUtc);
        }

        var written = await configurations.UpdateOneAsync(session, SourceBuildConfigurationFilters.AtRevision(current),
            update, cancellationToken: cancellationToken);
        var stored = await configurations.Find(session,
            SourceBuildConfigurationFilters.ById(current.TenantId, current.Id)).FirstOrDefaultAsync(cancellationToken);
        if (written.MatchedCount != 1)
            return stored is null ? new SourceBuildConfigurationNotFound(current.Id)
                : new SourceBuildConfigurationUpdateConflict(current.Id);

        if (current.Recipe != command.Recipe)
            await outbox(session, [OutboxWrite.Create(stored!.ToChangedEvent())], cancellationToken);

        return stored!;
    }
}
