using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Tenants;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Mongo;

internal static class TenantUpdates
{
    public static UpdateDefinition<Tenant> AddMember(TenantMember member) =>
        Builders<Tenant>.Update.Push(tenant => tenant.Members, member);

    public static UpdateDefinition<Tenant> SetMemberRole(TenantRole role) =>
        Builders<Tenant>.Update.Set("Members.$.Role", role);

    public static UpdateDefinition<Tenant> RemoveMember(string memberId) =>
        Builders<Tenant>.Update.PullFilter(
            tenant => tenant.Members,
            TenantFilters.MemberById(memberId));

    public static UpdateDefinition<Tenant> CreateIfMissing(Tenant tenant) =>
        Builders<Tenant>.Update
            .SetOnInsert(item => item.Name, tenant.Name)
            .SetOnInsert(item => item.CreatedAtUtc, tenant.CreatedAtUtc)
            .SetOnInsert(item => item.Members, tenant.Members);
}
