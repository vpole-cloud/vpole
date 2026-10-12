using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Persistence.Members;

internal static class TenantMemberMutationFailures
{
    public static async Task<TenantMemberMutationError> ResolveAsync(
        IMongoCollection<Tenant> tenants,
        IClientSessionHandle session,
        string tenantId,
        string memberId,
        CancellationToken cancellationToken)
    {
        var tenant = await tenants
            .Find(session, TenantFilters.ById(tenantId))
            .FirstOrDefaultAsync(cancellationToken);
        var member = tenant?.Members.FirstOrDefault(candidate => candidate.Id == memberId);
        if (tenant is null)
        {
            return new TenantNotFound(tenantId);
        }

        if (member is not null &&
            member.Role == TenantRole.Owner &&
            tenant.Members.Count(candidate => candidate.Role == TenantRole.Owner) == 1)
        {
            return new TenantLastOwner(tenantId, memberId);
        }

        return new TenantMemberNotFound(tenantId, memberId);
    }
}
