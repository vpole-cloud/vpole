using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

internal sealed class MongoCleanupSourceBuildConfigurations(
    IMongoCollection<SourceBuildConfiguration> configurations,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox)
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
            var deleted = await configurations.DeleteManyAsync(tx.Session, filter, cancellationToken: ct);
            if (deleted.DeletedCount != removed.Count)
                throw new InvalidOperationException("Source build configuration cleanup changed concurrently.");
            await outbox(tx.Session, removed.Select(configuration => OutboxWrite.Create(configuration.ToChangedEvent())).ToArray(), ct);
            return tx.Commit(new SourceBuildConfigurationCleanupRecorded(removed.Count));
        }, cancellationToken);
    }
}

internal sealed record SourceBuildConfigurationCleanupRecorded(int RemovedCount);
