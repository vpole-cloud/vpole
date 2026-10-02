using MongoDB.Driver;
using Pole.Database;
using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Persistence;

internal static class PrivateAccessFilters
{
    public static FilterDefinition<PrivateAccessGrant> Grant(string tenantId, string grantId) =>
        Builders<PrivateAccessGrant>.Filter.And(
            Builders<PrivateAccessGrant>.Filter.ForTenant(tenantId),
            Builders<PrivateAccessGrant>.Filter.Eq(item => item.Id, grantId));

    public static FilterDefinition<PrivateAccessGrant> GrantsForTenant(string tenantId) =>
        Builders<PrivateAccessGrant>.Filter.ForTenant(tenantId);

    public static FilterDefinition<PrivateAccessGrant> ActiveGrants(DateTime nowUtc) =>
        Builders<PrivateAccessGrant>.Filter.And(
            Builders<PrivateAccessGrant>.Filter.Gt(item => item.ExpiresAtUtc, nowUtc),
            Builders<PrivateAccessGrant>.Filter
                .OfType<PrivateAccessGrantState, ActivePrivateAccessGrant>(item => item.State));

    public static FilterDefinition<PrivateAccessGrant> OwnActiveGrants(
        string tenantId, string subjectUserId, DateTime nowUtc) =>
        Builders<PrivateAccessGrant>.Filter.And(
            GrantsForTenant(tenantId),
            Builders<PrivateAccessGrant>.Filter.Eq(item => item.SubjectUserId, subjectUserId),
            Builders<PrivateAccessGrant>.Filter.Gt(item => item.ExpiresAtUtc, nowUtc),
            Builders<PrivateAccessGrant>.Filter
                .OfType<PrivateAccessGrantState, ActivePrivateAccessGrant>(item => item.State));

    public static FilterDefinition<PrivateAccessGrant> OwnActiveResourceGrants(
        string tenantId,
        string subjectUserId,
        string resourceType,
        string resourceId,
        DateTime nowUtc) =>
        Builders<PrivateAccessGrant>.Filter.And(
            OwnActiveGrants(tenantId, subjectUserId, nowUtc),
            Builders<PrivateAccessGrant>.Filter.Eq(item => item.ResourceType, resourceType),
            Builders<PrivateAccessGrant>.Filter.Eq(item => item.ResourceId, resourceId));

    public static FilterDefinition<PrivateAccessGrant> GrantRevision(
        string tenantId, string grantId, long revision) =>
        Builders<PrivateAccessGrant>.Filter.And(
            Grant(tenantId, grantId),
            Builders<PrivateAccessGrant>.Filter.Eq(item => item.Revision, revision));

    public static FilterDefinition<PrivateAccessSession> Session(string tenantId, string sessionId) =>
        Builders<PrivateAccessSession>.Filter.And(
            Builders<PrivateAccessSession>.Filter.ForTenant(tenantId),
            Builders<PrivateAccessSession>.Filter.Eq(item => item.Id, sessionId));

    public static FilterDefinition<PrivateAccessSession> SessionId(string sessionId) =>
        Builders<PrivateAccessSession>.Filter.Eq(item => item.Id, sessionId);

    public static FilterDefinition<PrivateAccessSession> TicketHash(string ticketHash) =>
        Builders<PrivateAccessSession>.Filter.Eq(item => item.Ticket.Hash, ticketHash);

    public static FilterDefinition<PrivateAccessSession> SessionRevision(
        string tenantId, string sessionId, long revision) =>
        Builders<PrivateAccessSession>.Filter.And(
            Session(tenantId, sessionId),
            Builders<PrivateAccessSession>.Filter.Eq(item => item.Revision, revision));

    public static FilterDefinition<PrivateAccessSession> CleanupCandidates(DateTime nowUtc)
    {
        var filter = Builders<PrivateAccessSession>.Filter;
        return filter.Or(
            filter.OfType<PrivateAccessSessionState, PrivateAccessSessionCleaning>(
                item => item.State),
            filter.OfType<PrivateAccessSessionState, PrivateAccessSessionTerminated>(
                item => item.State),
            filter.And(
                filter.OfType<PrivateAccessSessionState, PrivateAccessTicketPending>(
                    item => item.State),
                filter.Lte(item => item.Ticket.ExpiresAtUtc, nowUtc)),
            filter.And(
                filter.OfType<PrivateAccessSessionState, PrivateAccessTicketRedeemed>(
                    item => item.State),
                filter.Or(
                    filter.Lte(item =>
                        ((PrivateAccessTicketRedeemed)item.State).RedeemedAtUtc,
                        nowUtc.AddSeconds(-30)),
                    filter.Lte(item => item.SessionDeadlineUtc, nowUtc))),
            filter.And(
                filter.OfType<PrivateAccessSessionState, PrivateAccessSessionActive>(
                    item => item.State),
                filter.Lte(item =>
                    ((PrivateAccessSessionActive)item.State).LastLeaseExpiresAtUtc,
                    nowUtc.AddSeconds(-30))));
    }
}
