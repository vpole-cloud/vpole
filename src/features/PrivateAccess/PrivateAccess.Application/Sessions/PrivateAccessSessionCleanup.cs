// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Application.Mongo;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;
using Pole.Workloads;
using PrivateAccessSessionCleaned = Pole.Features.PrivateAccess.Domain.PrivateAccessSessionCleaned;

namespace Pole.Features.PrivateAccess.Application.Sessions;

internal sealed record PrivateAccessCleanupClaim(
    PrivateAccessSessionCleanupSnapshot? Snapshot
);

internal sealed class MongoPrivateAccessSessionCleanup(
    IMongoCollection<PrivateAccessSession> sessions,
    IMongoCollection<PrivateAccessAudit> audit,
    ApplyDeploymentTargetReferenceChanges targetReferences,
    MongoRunTransaction transactions,
    TimeProvider timeProvider
)
{
    public async Task<IReadOnlyList<PrivateAccessSessionCleanupSnapshot>> LoadPageAsync(
        DateTime nowUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Cleanup time must be UTC.", nameof(nowUtc));
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit));

        // Claims are fenced per session after this candidate read. Stale candidates
        // cannot reach the caller because their revision must still match on claim.
        var candidates = await sessions.Find(PrivateAccessFilters.CleanupCandidates(nowUtc))
            .SortBy(item => item.CleanupLastAttemptAtUtc)
            .ThenBy(item => item.CreatedAtUtc)
            .Limit(limit)
            .ToListAsync(cancellationToken);
        var snapshots = new List<PrivateAccessSessionCleanupSnapshot>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var claim = await transactions.InvokeOutcomeAsync<PrivateAccessCleanupClaim>(
                async (transaction, ct) =>
                {
                    var current = await sessions.Find(
                            transaction.Session,
                            PrivateAccessFilters.Session(candidate.TenantId, candidate.Id))
                        .FirstOrDefaultAsync(ct);
                    if (current is null || current.Revision != candidate.Revision)
                        return transaction.Abort(new PrivateAccessCleanupClaim(null));
                    if (!IsEligible(current, nowUtc))
                        return transaction.Abort(new PrivateAccessCleanupClaim(null));

                    var reason = current.State switch
                    {
                        PrivateAccessTicketPending => "TicketExpired",
                        PrivateAccessTicketRedeemed => "SetupExpired",
                        PrivateAccessSessionActive => "LeaseExpired",
                        PrivateAccessSessionTerminated terminal => terminal.Reason,
                        PrivateAccessSessionCleaning cleaning => cleaning.Reason,
                        PrivateAccessSessionCleaned => throw new InvalidOperationException(
                            "A cleaned session cannot be claimed again."),
                    };
                    // Mongo stores DateTime values at millisecond precision. Advance by
                    // a full millisecond so repeated pages with the same now still rotate.
                    var attemptAtUtc = nowUtc;
                    if (current.CleanupLastAttemptAtUtc is DateTime previous)
                    {
                        var nextAttempt = previous.AddMilliseconds(1);
                        if (nextAttempt > attemptAtUtc)
                            attemptAtUtc = nextAttempt;
                    }

                    var changed = await sessions.ReplaceOneAsync(
                        transaction.Session,
                        PrivateAccessFilters.SessionRevision(current.TenantId, current.Id, current.Revision),
                        current with
                        {
                            State = new PrivateAccessSessionCleaning(nowUtc, reason),
                            CleanupLastAttemptAtUtc = attemptAtUtc,
                            Revision = checked(current.Revision + 1),
                        },
                        cancellationToken: ct);
                    if (changed.MatchedCount != 1)
                        return transaction.Abort(new PrivateAccessCleanupClaim(null));
                    if (current.State is not PrivateAccessSessionCleaning)
                        await audit.InsertOneAsync(
                            transaction.Session,
                            PrivateAccessAuditFactory.Create(
                                current.TenantId,
                                current.Grant.Id,
                                current.Id,
                                current.SubjectUserId,
                                nowUtc,
                                new PrivateAccessCleanupClaimedAudit()),
                            cancellationToken: ct);
                    return transaction.Commit(new PrivateAccessCleanupClaim(ToSnapshot(current)));
                },
                cancellationToken);
            if (claim.Snapshot is not null)
                snapshots.Add(claim.Snapshot);
        }

        return snapshots;
    }

    public Task<MarkPrivateAccessSessionCleanedOutcome> MarkCleanedAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<MarkPrivateAccessSessionCleanedOutcome>(
            async (transaction, ct) =>
            {
                var current = await sessions.Find(transaction.Session, PrivateAccessFilters.SessionId(sessionId))
                    .FirstOrDefaultAsync(ct);
                if (current is null)
                    return transaction.Abort(
                        new MarkPrivateAccessSessionCleanedOutcome(new PrivateAccessSessionUnavailable()));
                if (current.State is PrivateAccessSessionCleaned)
                    return transaction.Abort(
                        new MarkPrivateAccessSessionCleanedOutcome(new Contracts.PrivateAccessSessionCleaned()));
                if (current.State is not PrivateAccessSessionCleaning cleaning)
                    return transaction.Abort(
                        new MarkPrivateAccessSessionCleanedOutcome(new PrivateAccessSessionNotCleaning()));

                var now = timeProvider.GetUtcNow().UtcDateTime;
                var reference = await targetReferences(
                    new ApplyDeploymentTargetReferenceChangesRequest(
                        Owner: new DeploymentTargetReferenceOwner(
                            DeploymentTargetReferenceOwnerTypes.PrivateAccessSession,
                            current.Id),
                        AddedDeploymentTargetIds: [],
                        RemovedDeploymentTargetIds: [current.Route.DeploymentTargetId]),
                    session: transaction.Session,
                    cancellationToken: ct);
                if (reference is DeploymentTargetReferenceChangeRejected)
                    throw new InvalidOperationException("Private access target reference could not be released.");
                var changed = await sessions.ReplaceOneAsync(
                    transaction.Session,
                    PrivateAccessFilters.SessionRevision(current.TenantId, current.Id, current.Revision),
                    current with
                    {
                        State = new PrivateAccessSessionCleaned(now, cleaning.Reason),
                        Revision = checked(current.Revision + 1),
                    },
                    cancellationToken: ct);
                if (changed.MatchedCount != 1)
                    throw new InvalidOperationException("Session changed during cleanup acknowledgment.");
                await audit.InsertOneAsync(
                    transaction.Session,
                    PrivateAccessAuditFactory.Create(
                        current.TenantId,
                        current.Grant.Id,
                        current.Id,
                        current.SubjectUserId,
                        now,
                        new PrivateAccessCleanupCompletedAudit()),
                    cancellationToken: ct);
                return transaction.Commit(
                    new MarkPrivateAccessSessionCleanedOutcome(new Contracts.PrivateAccessSessionCleaned()));
            },
            cancellationToken);

    private static bool IsEligible(PrivateAccessSession session, DateTime nowUtc) => session.State switch
    {
        PrivateAccessTicketPending => session.Ticket.ExpiresAtUtc <= nowUtc,
        PrivateAccessTicketRedeemed redeemed => redeemed.RedeemedAtUtc.AddSeconds(30) <= nowUtc ||
            session.SessionDeadlineUtc <= nowUtc,
        PrivateAccessSessionActive active => active.LastLeaseExpiresAtUtc.AddSeconds(30) <= nowUtc,
        PrivateAccessSessionTerminated => true,
        PrivateAccessSessionCleaning => true,
        PrivateAccessSessionCleaned => false,
    };

    private static PrivateAccessSessionCleanupSnapshot ToSnapshot(PrivateAccessSession session) => new(
        session.Id,
        session.TenantId,
        session.Grant.Id,
        session.Route.DeploymentTargetId,
        session.Route.RuntimeCellId,
        session.Route.IsolationScopeId,
        session.Grant.ResourceDeploymentId,
        session.Route.KubernetesNamespace,
        session.Route.ServiceName,
        NetworkPort.Create(session.Route.ServicePort),
        NetworkPort.Create(session.Route.TargetPort));
}
