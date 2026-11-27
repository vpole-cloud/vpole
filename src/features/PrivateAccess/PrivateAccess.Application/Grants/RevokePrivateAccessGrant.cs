// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.PrivateAccess.Application.Mongo;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.PrivateAccess.Events;
using Pole.Features.Tenants.Contracts;

namespace Pole.Features.PrivateAccess.Application.Grants;

public sealed record PrivateAccessGrantRevoked;

public union PersistRevokePrivateAccessGrantOutcome(
    PrivateAccessGrantRevoked,
    PrivateAccessManageRoleRequired,
    PrivateAccessGrantMissing);

public sealed record PrivateAccessGrantMissing;

public delegate Task<PersistRevokePrivateAccessGrantOutcome> RevokePrivateAccessGrant(
    string tenantId,
    string grantId,
    string actorUserId,
    CancellationToken cancellationToken = default);

internal sealed class MongoRevokePrivateAccessGrant(
    RunMongoOutcomeTransaction<PersistRevokePrivateAccessGrantOutcome> transactions,
    LoadTenantMember loadTenantMember,
    IMongoCollection<PrivateAccessGrant> grants,
    IMongoCollection<PrivateAccessAudit> audit,
    StoreOutboxWrites storeOutboxWrites,
    TimeProvider timeProvider
)
{
    public Task<PersistRevokePrivateAccessGrantOutcome> InvokeAsync(
        string tenantId,
        string grantId,
        string actorUserId,
        CancellationToken cancellationToken = default) => transactions(
        async (transaction, ct) =>
        {
            var actor = await loadTenantMember(
                new LoadTenantMemberRequest(TenantId: tenantId, UserId: actorUserId),
                session: transaction.Session,
                cancellationToken: ct);
            if (actor is not TenantMemberSnapshot { Role: >= TenantMembershipRole.Admin })
                return transaction.Abort(
                    new PersistRevokePrivateAccessGrantOutcome(new PrivateAccessManageRoleRequired()));

            var grant = await grants.Find(transaction.Session, PrivateAccessFilters.Grant(tenantId, grantId))
                .FirstOrDefaultAsync(ct);
            if (grant is null)
                return transaction.Abort(new PersistRevokePrivateAccessGrantOutcome(new PrivateAccessGrantMissing()));
            if (grant.State is RevokedPrivateAccessGrant)
                return transaction.Abort(new PersistRevokePrivateAccessGrantOutcome(new PrivateAccessGrantRevoked()));

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var updated = grant.Revoke(actorUserId, now);
            var changed = await grants.ReplaceOneAsync(
                transaction.Session,
                PrivateAccessFilters.GrantRevision(tenantId, grantId, grant.Revision),
                updated,
                cancellationToken: ct);
            if (changed.ModifiedCount != 1)
                throw new InvalidOperationException("Grant changed during revocation transaction.");
            await audit.InsertOneAsync(
                transaction.Session,
                PrivateAccessAuditFactory.Create(
                    tenantId,
                    grantId,
                    null,
                    actorUserId,
                    now,
                    new PrivateAccessGrantRevokedAudit()),
                cancellationToken: ct);
            await storeOutboxWrites(
                transaction.Session,
                [
                    OutboxWrite.Create(
                        new PrivateAccessGrantRevokedEvent(grantId, updated.Revision)
                        {
                            EventCore = EventCore.Create(tenantId),
                        }),
                ],
                ct);
            return transaction.Commit(new PersistRevokePrivateAccessGrantOutcome(new PrivateAccessGrantRevoked()));
        },
        cancellationToken);
}
