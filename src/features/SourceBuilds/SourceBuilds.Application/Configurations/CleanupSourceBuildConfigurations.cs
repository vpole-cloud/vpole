using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Outbox.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Mongo;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Configurations;

internal sealed class MongoCleanupSourceBuildConfigurations(
    IMongoCollection<SourceBuildConfiguration> configurations,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox,
    IGitOpsMutations git)
{
    public async Task InvokeAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default)
    {
        await transactions.InvokeOutcomeAsync<SourceBuildConfigurationCleanupRecorded>(async (tx, ct) =>
            {
                var filter = SourceBuildConfigurationFilters.ByWebService(tenantId, webServiceId);
                var removed = await configurations.Find(tx.Session, filter).ToListAsync(ct);
                if (removed.Count == 0)
                    return tx.Commit(new SourceBuildConfigurationCleanupRecorded(0));
                var deleted = new List<SourceBuildConfiguration>();
                foreach (var configuration in removed)
                {
                    if (!await git.AuthorizeAsync(new(tenantId, "SourceBuildConfiguration", configuration.Id),
                            tx.Session, ct))
                        continue;

                    var result = await configurations.DeleteOneAsync(tx.Session,
                        SourceBuildConfigurationFilters.AtPlanningVersion(configuration), cancellationToken: ct);
                    if (result.DeletedCount != 1)
                        throw new InvalidOperationException("Source build configuration cleanup changed concurrently.");
                    deleted.Add(configuration);
                }
                if (deleted.Count != 0)
                    await outbox(tx.Session,
                        deleted.Select(configuration => OutboxWrite.Create(configuration.ToChangedEvent())).ToArray(),
                        ct);
                return tx.Commit(new SourceBuildConfigurationCleanupRecorded(deleted.Count));
            },
            cancellationToken);
    }
}

internal sealed record SourceBuildConfigurationCleanupRecorded(int RemovedCount);
