using MongoDB.Driver;
using Pole.Database;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Application.Common;
using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Persistence;

internal sealed class MongoPrivateAccessSessionAdmission(
    IMongoCollection<PrivateAccessSession> sessions,
    IMongoCollection<PrivateAccessAudit> audit,
    PrivateAccessAdmission admission,
    MongoRunTransaction transactions,
    TimeProvider timeProvider)
{
    public async Task<PrivateAccessTicketRoute?> LocateAsync(
        string ticket,
        CancellationToken cancellationToken = default)
    {
        if (!PrivateAccessTicketCodec.TryHash(ticket, out var hash))
            return null;
        var current = await sessions.Find(PrivateAccessFilters.TicketHash(hash))
            .FirstOrDefaultAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return current is { State: PrivateAccessTicketPending } &&
               current.Ticket.ExpiresAtUtc > now && current.SessionDeadlineUtc > now
            ? new PrivateAccessTicketRoute(current.TenantId, current.Route.DeploymentTargetId)
            : null;
    }

    public Task<RedeemPrivateAccessSessionOutcome> RedeemAsync(
        string ticket,
        CancellationToken cancellationToken = default)
    {
        if (!PrivateAccessTicketCodec.TryHash(ticket, out var hash))
            return Task.FromResult<RedeemPrivateAccessSessionOutcome>(
                new PrivateAccessTicketInvalid());

        return transactions.InvokeOutcomeAsync<RedeemPrivateAccessSessionOutcome>(
            async (transaction, ct) =>
            {
                var current = await sessions.Find(transaction.Session,
                        PrivateAccessFilters.TicketHash(hash))
                    .FirstOrDefaultAsync(ct);
                var now = timeProvider.GetUtcNow().UtcDateTime;
                if (current is null || current.Ticket.ExpiresAtUtc <= now ||
                    current.SessionDeadlineUtc <= now)
                    return transaction.Abort(new RedeemPrivateAccessSessionOutcome(
                        new PrivateAccessTicketInvalid()));
                if (current.State is not PrivateAccessTicketPending)
                    return transaction.Abort(new RedeemPrivateAccessSessionOutcome(
                        new PrivateAccessTicketUsed()));

                var check = await admission.CheckAsync(current, transaction.Session, ct);
                if (check is PrivateAccessLeaseSnapshot lease)
                {
                    await ReplaceAsync(current,
                        current with
                        {
                            State = new PrivateAccessTicketRedeemed(now, null)
                        },
                        transaction.Session, ct);
                    await AuditAsync(current, new PrivateAccessSessionRedeemedAudit(), now, transaction.Session, ct);
                    return transaction.Commit(new RedeemPrivateAccessSessionOutcome(lease));
                }

                await ReplaceAsync(current,
                    current with
                    {
                        State = new PrivateAccessSessionTerminated(now, "AdmissionDenied")
                    }, transaction.Session, ct);
                await AuditAsync(current, new PrivateAccessSessionDeniedAudit(), now, transaction.Session, ct);
                var denied = check switch
                {
                    PrivateAccessGrantUnavailable error => new RedeemPrivateAccessSessionOutcome(error),
                    PrivateAccessSubjectMembershipRequired error => new RedeemPrivateAccessSessionOutcome(error),
                    PrivateAccessDeploymentUnavailable error => new RedeemPrivateAccessSessionOutcome(error),
                    PrivateAccessEndpointUnavailable error => new RedeemPrivateAccessSessionOutcome(error),
                    PrivateAccessBindingChanged error => new RedeemPrivateAccessSessionOutcome(error),
                    PrivateAccessLeaseSnapshot => throw new InvalidOperationException()
                };
                return transaction.Commit(denied);
            }, cancellationToken);
    }

    public Task<RenewPrivateAccessSessionOutcome> RenewAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<RenewPrivateAccessSessionOutcome>(
            async (transaction, ct) =>
            {
                var current = await sessions.Find(transaction.Session,
                        PrivateAccessFilters.SessionId(sessionId))
                    .FirstOrDefaultAsync(ct);
                var now = timeProvider.GetUtcNow().UtcDateTime;
                if (current is null || current.SessionDeadlineUtc <= now)
                    return transaction.Abort(new RenewPrivateAccessSessionOutcome(
                        new PrivateAccessSessionUnavailable()));

                var renewable = current.State switch
                {
                    PrivateAccessTicketRedeemed redeemed =>
                        redeemed.RedeemedAtUtc.AddSeconds(30) > now &&
                        (redeemed.LastAuthorizationExpiresAtUtc is null ||
                         redeemed.LastAuthorizationExpiresAtUtc > now),
                    PrivateAccessSessionActive active =>
                        active.LastLeaseExpiresAtUtc > now,
                    PrivateAccessTicketPending or PrivateAccessSessionTerminated or
                        PrivateAccessSessionCleaning or
                        Pole.Features.PrivateAccess.Domain.PrivateAccessSessionCleaned => false
                };
                if (!renewable)
                    return transaction.Abort(new RenewPrivateAccessSessionOutcome(
                        new PrivateAccessSessionUnavailable()));

                var check = await admission.CheckAsync(current, transaction.Session, ct);
                if (check is PrivateAccessLeaseSnapshot lease)
                {
                    var renewedState = current.State switch
                    {
                        PrivateAccessTicketRedeemed redeemed =>
                            (PrivateAccessSessionState)new PrivateAccessTicketRedeemed(
                                redeemed.RedeemedAtUtc, lease.ExpiresAtUtc),
                        PrivateAccessSessionActive active =>
                            new PrivateAccessSessionActive(
                                active.RedeemedAtUtc, active.StartedAtUtc,
                                lease.ExpiresAtUtc),
                        PrivateAccessTicketPending or PrivateAccessSessionTerminated or
                            PrivateAccessSessionCleaning or
                            Pole.Features.PrivateAccess.Domain.PrivateAccessSessionCleaned =>
                            throw new InvalidOperationException("Only redeemed or active sessions can renew.")
                    };
                    await ReplaceAsync(current,
                        current with { State = renewedState }, transaction.Session, ct);
                    return transaction.Commit(new RenewPrivateAccessSessionOutcome(lease));
                }

                await ReplaceAsync(current,
                    current with
                    {
                        State = new PrivateAccessSessionTerminated(now, "RenewalDenied")
                    }, transaction.Session, ct);
                await AuditAsync(current, new PrivateAccessSessionDeniedAudit(), now, transaction.Session, ct);
                var denied = check switch
                {
                    PrivateAccessGrantUnavailable error => new RenewPrivateAccessSessionOutcome(error),
                    PrivateAccessSubjectMembershipRequired error => new RenewPrivateAccessSessionOutcome(error),
                    PrivateAccessDeploymentUnavailable error => new RenewPrivateAccessSessionOutcome(error),
                    PrivateAccessEndpointUnavailable error => new RenewPrivateAccessSessionOutcome(error),
                    PrivateAccessBindingChanged error => new RenewPrivateAccessSessionOutcome(error),
                    PrivateAccessLeaseSnapshot => throw new InvalidOperationException()
                };
                return transaction.Commit(denied);
            }, cancellationToken);

    public Task<MarkPrivateAccessSessionStartedOutcome> MarkStartedAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<MarkPrivateAccessSessionStartedOutcome>(
            async (transaction, ct) =>
            {
                var current = await FindSessionAsync(sessionId, transaction.Session, ct);
                var now = timeProvider.GetUtcNow().UtcDateTime;
                if (current is null || current.SessionDeadlineUtc <= now)
                    return transaction.Abort(new MarkPrivateAccessSessionStartedOutcome(
                        new PrivateAccessSessionUnavailable()));
                if (current.State is PrivateAccessSessionActive active)
                    return active.LastLeaseExpiresAtUtc > now
                        ? transaction.Abort(new MarkPrivateAccessSessionStartedOutcome(
                            new PrivateAccessSessionStarted()))
                        : transaction.Abort(new MarkPrivateAccessSessionStartedOutcome(
                            new PrivateAccessSessionUnavailable()));
                if (current.State is not PrivateAccessTicketRedeemed redeemed)
                    return transaction.Abort(new MarkPrivateAccessSessionStartedOutcome(
                        new PrivateAccessSessionUnavailable()));
                if (redeemed.RedeemedAtUtc.AddSeconds(30) <= now ||
                    redeemed.LastAuthorizationExpiresAtUtc is null ||
                    redeemed.LastAuthorizationExpiresAtUtc <= now)
                    return transaction.Abort(new MarkPrivateAccessSessionStartedOutcome(
                        new PrivateAccessSessionUnavailable()));

                var check = await admission.CheckAsync(current, transaction.Session, ct);
                if (check is not PrivateAccessLeaseSnapshot lease)
                {
                    await ReplaceAsync(current,
                        current with
                        {
                            State = new PrivateAccessSessionTerminated(now, "StartDenied")
                        }, transaction.Session, ct);
                    await AuditAsync(current, new PrivateAccessSessionDeniedAudit(), now, transaction.Session, ct);
                    return transaction.Commit(new MarkPrivateAccessSessionStartedOutcome(
                        new PrivateAccessSessionUnavailable()));
                }

                await ReplaceAsync(current,
                    current with
                    {
                        State = new PrivateAccessSessionActive(
                            redeemed.RedeemedAtUtc, now, lease.ExpiresAtUtc)
                    }, transaction.Session, ct);
                await AuditAsync(current, new PrivateAccessSessionStartedAudit(), now, transaction.Session, ct);
                return transaction.Commit(new MarkPrivateAccessSessionStartedOutcome(
                    new PrivateAccessSessionStarted()));
            }, cancellationToken);

    public Task<ClosePrivateAccessSessionOutcome> CloseAsync(
        string sessionId,
        PrivateAccessSessionCloseReason reason,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<ClosePrivateAccessSessionOutcome>(
            async (transaction, ct) =>
            {
                if (!Enum.IsDefined(reason))
                    throw new ArgumentOutOfRangeException(nameof(reason));
                var current = await FindSessionAsync(sessionId, transaction.Session, ct);
                if (current is null)
                    return transaction.Abort(new ClosePrivateAccessSessionOutcome(
                        new PrivateAccessSessionUnavailable()));
                if (current.State is PrivateAccessSessionTerminated or
                    PrivateAccessSessionCleaning or
                    Pole.Features.PrivateAccess.Domain.PrivateAccessSessionCleaned)
                    return transaction.Abort(new ClosePrivateAccessSessionOutcome(
                        new PrivateAccessSessionClosed()));

                var now = timeProvider.GetUtcNow().UtcDateTime;
                await ReplaceAsync(current,
                    current with
                    {
                        State = new PrivateAccessSessionTerminated(now, reason.ToString())
                    }, transaction.Session, ct);
                await AuditAsync(current, new PrivateAccessSessionClosedAudit(), now, transaction.Session, ct);
                return transaction.Commit(new ClosePrivateAccessSessionOutcome(
                    new PrivateAccessSessionClosed()));
            }, cancellationToken);

    private async Task<PrivateAccessSession?> FindSessionAsync(
        string sessionId,
        IClientSessionHandle session,
        CancellationToken cancellationToken) =>
        await sessions.Find(session,
                PrivateAccessFilters.SessionId(sessionId))
            .FirstOrDefaultAsync(cancellationToken);

    private async Task ReplaceAsync(
        PrivateAccessSession current,
        PrivateAccessSession updated,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var result = await sessions.ReplaceOneAsync(
            session,
            PrivateAccessFilters.SessionRevision(
                current.TenantId, current.Id, current.Revision),
            updated with { Revision = checked(current.Revision + 1) },
            cancellationToken: cancellationToken);
        if (result.MatchedCount != 1)
            throw new InvalidOperationException("Private access session changed during transaction.");
    }

    private Task AuditAsync(
        PrivateAccessSession current,
        PrivateAccessAuditState state,
        DateTime now,
        IClientSessionHandle session,
        CancellationToken cancellationToken) =>
        audit.InsertOneAsync(session,
            PrivateAccessAuditFactory.Create(
                current.TenantId,
                current.Grant.Id, current.Id, current.SubjectUserId, now, state),
            cancellationToken: cancellationToken);
}
