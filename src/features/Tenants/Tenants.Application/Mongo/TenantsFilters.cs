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
