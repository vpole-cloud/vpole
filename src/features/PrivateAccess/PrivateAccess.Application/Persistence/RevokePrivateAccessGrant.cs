using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.PrivateAccess.Events;
using Pole.Features.Tenants.Contracts;

namespace Pole.Features.PrivateAccess.Application.Persistence;

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
    TimeProvider timeProvider)
{
    public Task<PersistRevokePrivateAccessGrantOutcome> InvokeAsync(
        string tenantId,
        string grantId,
        string actorUserId,
        CancellationToken cancellationToken = default) =>
        transactions(async (transaction, ct) =>
        {
            var actor = await loadTenantMember(tenantId, actorUserId,
                transaction.Session, ct);
            if (actor is not TenantMemberSnapshot { Role: >= TenantMembershipRole.Admin })
                return transaction.Abort(new PersistRevokePrivateAccessGrantOutcome(
                    new PrivateAccessManageRoleRequired()));

            var grant = await grants.Find(transaction.Session,
                    PrivateAccessFilters.Grant(tenantId, grantId))
                .FirstOrDefaultAsync(ct);
            if (grant is null)
                return transaction.Abort(new PersistRevokePrivateAccessGrantOutcome(
                    new PrivateAccessGrantMissing()));
            if (grant.State is RevokedPrivateAccessGrant)
                return transaction.Abort(new PersistRevokePrivateAccessGrantOutcome(
                    new PrivateAccessGrantRevoked()));

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var updated = grant.Revoke(actorUserId, now);
            var changed = await grants.ReplaceOneAsync(
                transaction.Session,
                PrivateAccessFilters.GrantRevision(tenantId, grantId, grant.Revision),
                updated,
                cancellationToken: ct);
            if (changed.ModifiedCount != 1)
                throw new InvalidOperationException("Grant changed during revocation transaction.");
            await audit.InsertOneAsync(transaction.Session, new PrivateAccessAudit(
                ObjectId.GenerateNewId().ToString(), tenantId, grantId,
                null, actorUserId, PrivateAccessAuditAction.GrantRevoked, now), cancellationToken: ct);
            await storeOutboxWrites(transaction.Session,
                [OutboxWrite.Create(new PrivateAccessGrantRevokedEvent(grantId, updated.Revision)
                {
                    EventCore = EventCore.Create(tenantId)
                })], ct);
            return transaction.Commit(new PersistRevokePrivateAccessGrantOutcome(
                new PrivateAccessGrantRevoked()));
        }, cancellationToken);
}
