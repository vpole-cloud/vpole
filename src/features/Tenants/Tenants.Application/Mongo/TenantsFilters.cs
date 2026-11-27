// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Mongo;

internal static class TenantFilters
{
    public static FilterDefinition<TenantMember> MemberById(string memberId) =>
        Builders<TenantMember>.Filter.Eq(member => member.Id, memberId);

    public static FilterDefinition<Tenant> ById(string tenantId) =>
        Builders<Tenant>.Filter.Eq(tenant => tenant.Id, tenantId);

    public static FilterDefinition<Tenant> ByIds(IReadOnlyCollection<string> tenantIds) =>
        Builders<Tenant>.Filter.In(tenant => tenant.Id, tenantIds);

    public static FilterDefinition<Tenant> ForMemberUser(string userId) => Builders<Tenant>.Filter.ElemMatch(
        tenant => tenant.Members,
        member => member.UserId == userId);

    public static FilterDefinition<Tenant> ByIdForMemberUser(string tenantId, string userId) =>
        ById(tenantId) & ForMemberUser(userId);

    public static FilterDefinition<Tenant> ByIdWithoutMemberUser(string tenantId, string userId) =>
        Builders<Tenant>.Filter.And(ById(tenantId), Builders<Tenant>.Filter.Not(ForMemberUser(userId)));

    public static FilterDefinition<Tenant> ByIdWithMember(string tenantId, string memberId) =>
        Builders<Tenant>.Filter.And(
            ById(tenantId),
            Builders<Tenant>.Filter.ElemMatch(tenant => tenant.Members, member => member.Id == memberId));

    public static FilterDefinition<Tenant> ByIdWithMemberAndOwnerCanBeRemoved(string tenantId, string memberId) =>
        Builders<Tenant>.Filter.And(
            ByIdWithMember(tenantId, memberId),
            Builders<Tenant>.Filter.Or(
                Builders<Tenant>.Filter.ElemMatch(
                    tenant => tenant.Members,
                    member => member.Id == memberId && member.Role != TenantRole.Owner),
                AtLeastTwoOwners()));

    private static FilterDefinition<Tenant> AtLeastTwoOwners() => Builders<Tenant>.Filter.Where(tenant =>
        tenant.Members.Count(member => member.Role == TenantRole.Owner) > 1);
}

internal static class InvitationFilters
{
    public static FilterDefinition<TenantInvitation> ById(string id) =>
        Builders<TenantInvitation>.Filter.Eq(item => item.Id, id);

    public static FilterDefinition<TenantInvitation> ForTenantById(string tenantId, string id) =>
        ByTenant(tenantId) & ById(id);

    public static FilterDefinition<TenantInvitation> ByTenant(string tenantId) =>
        Builders<TenantInvitation>.Filter.Eq(item => item.TenantId, tenantId);

    public static FilterDefinition<TenantInvitation> ByTokenHash(string hash) =>
        Builders<TenantInvitation>.Filter.Eq(item => item.TokenHash, hash);

    public static FilterDefinition<TenantInvitation> AtRevision(string id, long revision) =>
        ById(id) & Builders<TenantInvitation>.Filter.Eq(item => item.Revision, revision);

    public static FilterDefinition<TenantInvitation> ForTenantAtRevision(
        string? tenantId,
        string id,
        long revision) => AtRevision(id, revision) &
        Builders<TenantInvitation>.Filter.Eq(item => item.TenantId, tenantId);

    public static FilterDefinition<TenantInvitation> PendingAtRevision(
        string tenantId,
        string id,
        long revision) => ForTenantAtRevision(tenantId, id, revision) &
        Builders<TenantInvitation>.Filter.Eq(item => item.Status, TenantInvitationStatus.Pending);

    public static FilterDefinition<TenantInvitation> UnsentAtRevision(
        string tenantId,
        string id,
        long revision) => PendingAtRevision(tenantId, id, revision) & Builders<TenantInvitation>.Filter.Ne(
        item => item.DeliveryStatus,
        InvitationDeliveryStatus.Sent);

    public static FilterDefinition<TenantInvitationAcceptance> Receipt(string hash) =>
        Builders<TenantInvitationAcceptance>.Filter.Eq(item => item.Id, hash);
}
