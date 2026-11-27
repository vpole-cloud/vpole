// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceConnections.Events;

namespace Pole.Features.ResourceConnections.Application.Connections;

public sealed record ResourceConnectionDeletionConflict(
    string Id
);

public sealed record ResourceConnectionGitOwned(
    string Id
);

public union DeleteResourceConnectionPersistenceOutcome(
    ResourceConnection,
    ResourceConnectionMissing,
    ResourceConnectionDeletionConflict,
    ResourceConnectionGitOwned);

public sealed record ResourceConnectionMissing(
    string Id
);

public sealed record BeginResourceConnectionDeletionRequest(
    string TenantId,
    string ConnectionId,
    ResourceConnectionPlanningVersion? Expected = null,
    GitLifecycleAuthority? Lifecycle = null
);

public delegate Task<DeleteResourceConnectionPersistenceOutcome> BeginResourceConnectionDeletion(
    string tenantId,
    string connectionId,
    CancellationToken cancellationToken = default);

internal sealed class MongoBeginResourceConnectionDeletion(
    IMongoCollection<ResourceConnection> connections,
    RunMongoOutcomeTransaction<DeleteResourceConnectionPersistenceOutcome> transactions,
    StoreOutboxWrites storeOutboxWrites,
    IGitOpsMutations git,
    TimeProvider timeProvider
)
{
    public Task<DeleteResourceConnectionPersistenceOutcome> InvokeAsync(
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken = default) => transactions(
        async (transaction, ct) =>
        {
            var outcome = await ExecuteAsync(
                new BeginResourceConnectionDeletionRequest(tenantId, connectionId),
                transaction.Session,
                ct);
            return outcome is ResourceConnection ? transaction.Commit(outcome) : transaction.Abort(outcome);
        },
        cancellationToken);

    internal async Task<DeleteResourceConnectionPersistenceOutcome> ExecuteAsync(
        BeginResourceConnectionDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Connection deletion requires a caller-owned transaction.");

        var current = await connections.Find(
                session,
                ResourceConnectionFilters.ByTenantAndId(request.TenantId, request.ConnectionId))
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return new ResourceConnectionMissing(request.ConnectionId);

        if (request.Expected is { } expected && (expected.ConnectionId != current.Id ||
                expected.Revision != current.Revision || expected.ReferenceFence != current.ReferenceFence))
            return new ResourceConnectionDeletionConflict(request.ConnectionId);

        if (!await git.AuthorizeAsync(
                new AuthorizeGitResourceMutationRequest(
                    request.TenantId,
                    "ResourceConnection",
                    request.ConnectionId,
                    Lifecycle: request.Lifecycle,
                    LifecycleEffect: GitLifecycleMutationEffect.Remove),
                session,
                ct))
            return new ResourceConnectionGitOwned(request.ConnectionId);

        if (current.Lifecycle is DeletingResourceConnection)
            return current;

        var deleting = current.BeginDeletion(timeProvider.GetUtcNow().UtcDateTime);
        var replaced = await connections.ReplaceOneAsync(
            session,
            ResourceConnectionFilters.AtPlanningVersion(
                request.TenantId,
                new ResourceConnectionPlanningVersion(current.Id, current.Revision, current.ReferenceFence)),
            deleting,
            cancellationToken: ct);
        if (replaced.ModifiedCount != 1)
            return new ResourceConnectionDeletionConflict(request.ConnectionId);

        await storeOutboxWrites(
            session,
            [
                OutboxWrite.Create(
                    new ResourceConnectionDeletionRequestedEvent(current.Id, deleting.Generation)
                    {
                        EventCore = EventCore.Create(request.TenantId),
                    }),
            ],
            ct);
        return deleting;
    }
}
