using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.PrivateAccess.Application.Admission;
using Pole.Features.PrivateAccess.Application.Catalog;
using Pole.Features.PrivateAccess.Application.Gateway;
using Pole.Features.PrivateAccess.Application.Grants;
using Pole.Features.PrivateAccess.Application.Sessions;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Mongo;

internal static class PrivateAccessBrokerOwnerFilters
{
    public static FilterDefinition<PrivateAccessBrokerOwner> ByGateway(string gatewayId) =>
        Builders<PrivateAccessBrokerOwner>.Filter.Eq(item => item.Id, gatewayId);

    public static FilterDefinition<PrivateAccessBrokerOwner> Current(
        PrivateAccessBrokerOwner owner) =>
        Builders<PrivateAccessBrokerOwner>.Filter.And(
            ByGateway(owner.Id),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(item => item.Epoch, owner.Epoch),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.GatewayGeneration, owner.GatewayGeneration),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.GatewayRevision, owner.GatewayRevision));

    public static FilterDefinition<PrivateAccessBrokerOwner> Claimable(
        PrivateAccessBrokerOwner owner,
        string instanceId) =>
        Builders<PrivateAccessBrokerOwner>.Filter.And(
            Current(owner),
            Builders<PrivateAccessBrokerOwner>.Filter.Or(
                Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                    item => item.InstanceId, instanceId),
                Builders<PrivateAccessBrokerOwner>.Filter.AtOrBeforeServerNow(
                    item => item.LeaseExpiresAtUtc)));

    public static FilterDefinition<PrivateAccessBrokerOwner> Lease(
        PrivateAccessBrokerOwnerLease lease) =>
        Builders<PrivateAccessBrokerOwner>.Filter.And(
            ByGateway(lease.Gateway.GatewayId),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.TenantId, lease.Gateway.TenantId),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.DeploymentTargetId, lease.Gateway.DeploymentTargetId),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.GatewayGeneration, lease.Gateway.Generation),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.GatewayRevision, lease.Gateway.Revision),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(item => item.InstanceId, lease.InstanceId),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(item => item.Epoch, lease.Epoch));

    public static FilterDefinition<PrivateAccessBrokerOwner> ActiveLease(
        PrivateAccessBrokerOwnerLease lease) =>
        Builders<PrivateAccessBrokerOwner>.Filter.And(Lease(lease),
            Builders<PrivateAccessBrokerOwner>.Filter.AfterServerNow(item => item.LeaseExpiresAtUtc));

    public static FilterDefinition<PrivateAccessBrokerOwner> ActiveForGateway(
        string gatewayId,
        string tenantId,
        string targetId) =>
        Builders<PrivateAccessBrokerOwner>.Filter.And(
            ByGateway(gatewayId),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(item => item.TenantId, tenantId),
            Builders<PrivateAccessBrokerOwner>.Filter.Eq(
                item => item.DeploymentTargetId, targetId),
            Builders<PrivateAccessBrokerOwner>.Filter.Ne(item => item.InstanceId, null),
            Builders<PrivateAccessBrokerOwner>.Filter.Ne(item => item.InternalOrigin, null),
            Builders<PrivateAccessBrokerOwner>.Filter.AfterServerNow(item => item.LeaseExpiresAtUtc));
}

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

    public static FilterDefinition<PrivateAccessGrant> ActiveGrantsForTenant(
        string tenantId,
        DateTime nowUtc) =>
        Builders<PrivateAccessGrant>.Filter.And(
            GrantsForTenant(tenantId),
            ActiveGrants(nowUtc));

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

internal static class PrivateAccessGatewayFilters
{
    public static FilterDefinition<PrivateAccessGateway> ByTenantTarget(
        string tenantId,
        string targetId) =>
        Builders<PrivateAccessGateway>.Filter.And(
            Builders<PrivateAccessGateway>.Filter.ForTenant(tenantId),
            Builders<PrivateAccessGateway>.Filter.Eq(item => item.DeploymentTargetId, targetId));

    public static FilterDefinition<PrivateAccessGateway> ActivePin(
        string certificateSha256Pin,
        DateTime nowUtc) =>
        Builders<PrivateAccessGateway>.Filter.And(
            Builders<PrivateAccessGateway>.Filter.Eq(
                item => item.CertificateSha256Pin, certificateSha256Pin),
            Builders<PrivateAccessGateway>.Filter.Gt(
                item => item.CertificateNotAfterUtc, nowUtc),
            Builders<PrivateAccessGateway>.Filter
                .OfType<PrivateAccessGatewayState, ActivePrivateAccessGatewayState>(
                    item => item.State));

    public static FilterDefinition<PrivateAccessGateway> Current(
        string tenantId,
        string targetId,
        string gatewayId,
        long generation,
        long revision) =>
        Builders<PrivateAccessGateway>.Filter.And(
            ByTenantTarget(tenantId, targetId),
            Builders<PrivateAccessGateway>.Filter.Eq(item => item.Id, gatewayId),
            Builders<PrivateAccessGateway>.Filter.Eq(item => item.Generation, generation),
            Builders<PrivateAccessGateway>.Filter.Eq(item => item.Revision, revision),
            Builders<PrivateAccessGateway>.Filter
                .OfType<PrivateAccessGatewayState, ActivePrivateAccessGatewayState>(
                    item => item.State));
}
