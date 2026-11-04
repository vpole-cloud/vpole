using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceConnections.Events;

namespace Pole.Features.ResourceConnections.Application.Persistence;

public sealed record ResourceConnectionDeletionConflict(string Id);

public sealed record ResourceConnectionGitOwned(string Id);

public union DeleteResourceConnectionPersistenceOutcome(
    ResourceConnection,
    ResourceConnectionMissing,
    ResourceConnectionDeletionConflict,
    ResourceConnectionGitOwned);

public sealed record ResourceConnectionMissing(string Id);

public delegate Task<DeleteResourceConnectionPersistenceOutcome> BeginResourceConnectionDeletion(
    string tenantId,
    string connectionId,
    CancellationToken cancellationToken = default);

internal sealed class MongoBeginResourceConnectionDeletion(
    IMongoCollection<ResourceConnection> connections,
    RunMongoOutcomeTransaction<DeleteResourceConnectionPersistenceOutcome> transactions,
    StoreOutboxWrites storeOutboxWrites,
    IGitOpsMutations git,
    TimeProvider timeProvider)
{
    public Task<DeleteResourceConnectionPersistenceOutcome> InvokeAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken = default) =>
        transactions(async (transaction, ct) =>
        {
            var current = await connections.Find(
                    transaction.Session,
                    ResourceConnectionFilters.ByTenantAndId(tenantId, connectionId))
                .FirstOrDefaultAsync(ct);
            if (current is null)
                return transaction.Abort(new DeleteResourceConnectionPersistenceOutcome(
                    new ResourceConnectionMissing(connectionId)));
            if (!await git.AuthorizeAsync(new(tenantId, "ResourceConnection", connectionId), transaction.Session, ct))
                return transaction.Abort(new DeleteResourceConnectionPersistenceOutcome(
                    new ResourceConnectionGitOwned(connectionId)));

            if (current.Lifecycle is DeletingResourceConnection)
                return transaction.Commit(new DeleteResourceConnectionPersistenceOutcome(current));

            var deleting = current.BeginDeletion(timeProvider.GetUtcNow().UtcDateTime);
            var replaced = await connections.ReplaceOneAsync(
                transaction.Session,
                ResourceConnectionFilters.AtRevision(
                    tenantId,
                    connectionId,
                    current.Revision),
                deleting,
                cancellationToken: ct);
            if (replaced.ModifiedCount != 1)
                return transaction.Abort(new DeleteResourceConnectionPersistenceOutcome(
                    new ResourceConnectionDeletionConflict(connectionId)));

            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(new ResourceConnectionDeletionRequestedEvent(
                    connectionId,
                    deleting.Generation)
                {
                    EventCore = EventCore.Create(tenantId)
                })],
                ct);
            return transaction.Commit(new DeleteResourceConnectionPersistenceOutcome(deleting));
        }, cancellationToken);
}
