using Pole.Features.ResourceConnections.Application.Topology;
using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Common;
using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Domain;

namespace Pole.Features.ResourceConnections.Application.Recovery;

public delegate Task<ResourceConnectionCleanupTombstone?> LoadResourceConnectionTombstone(
    string tenantId,
    string connectionId,
    CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<ResourceConnectionCleanupTombstone>>
    LoadResourceConnectionTombstoneRecoveryPage(
        string? afterId,
        int pageSize,
        CancellationToken cancellationToken = default);

internal sealed class MongoResourceConnectionTombstonePersistence(
    IMongoCollection<ResourceConnection> connections,
    IMongoCollection<ResourceConnectionCleanupTombstone> tombstones,
    RunMongoOutcomeTransaction<bool> transactions,
    TimeProvider timeProvider,
    StoreOutboxWrites outbox)
{
    public async Task<ResourceConnectionCleanupTombstone?> LoadAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken = default) =>
        await tombstones.Find(ResourceConnectionTombstoneFilters.ByTenantAndId(
                tenantId,
                connectionId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ResourceConnectionCleanupTombstone>> LoadPageAsync(
        string? afterId,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        await tombstones.Find(ResourceConnectionTombstoneFilters.AfterId(afterId))
            .SortBy(item => item.Id)
            .Limit(pageSize)
            .ToListAsync(cancellationToken);

    public Task PurgeIfCompleteAsync(
        ResourceConnection observed,
        CancellationToken cancellationToken = default) =>
        transactions(async (transaction, ct) =>
            {
                var current = await connections.Find(
                        transaction.Session,
                        ResourceConnectionFilters.AtRevision(
                            observed.TenantId,
                            observed.Id,
                            observed.Revision))
                    .FirstOrDefaultAsync(ct);
                if (current is null || !current.IsCleanupComplete)
                    return transaction.Commit(false);

                var deleting = (DeletingResourceConnection)current.Lifecycle;
                var tombstone = new ResourceConnectionCleanupTombstone(
                    current.Id,
                    current.TenantId,
                    current.Generation,
                    deleting.Targets.Select(target => target.Snapshot).ToImmutableList(),
                    timeProvider.GetUtcNow().UtcDateTime);
                await tombstones.InsertOneAsync(
                    transaction.Session,
                    tombstone,
                    cancellationToken: ct);
                var removed = await connections.DeleteOneAsync(
                    transaction.Session,
                    ResourceConnectionFilters.AtRevision(
                        current.TenantId,
                        current.Id,
                        current.Revision),
                    cancellationToken: ct);
                if (removed.DeletedCount != 1)
                    return transaction.Abort(false);
                await outbox(transaction.Session,
                    [OutboxWrite.Create((current with { Revision = checked(current.Revision + 1) }).ToChangedEvent())],
                    ct);
                return transaction.Commit(true);
            },
            cancellationToken);
}
