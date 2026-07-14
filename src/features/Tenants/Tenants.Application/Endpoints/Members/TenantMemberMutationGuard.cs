using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Endpoints;

internal static class TenantMemberMutationGuard
{
    public static async Task<Failure?> ValidateAsync(
        ITenantDbContext dbContext,
        string tenantId,
        string memberId,
        TenantRole? nextRole,
        CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants
            .Find(t => t.Id == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null)
            return Failures.NotFound("Tenant not found.", tenantId);

        var existingMember = tenant.Members.FirstOrDefault(m => m.Id == memberId);
        if (existingMember is null)
            return Failures.NotFound("Tenant member not found.", memberId);

        var removesOwner = nextRole is null || nextRole != TenantRole.Owner;
        if (existingMember.Role == TenantRole.Owner &&
            removesOwner &&
            tenant.Members.Count(m => m.Role == TenantRole.Owner) == 1)
        {
            return Failures.Conflict("Tenant must keep at least one owner.");
        }

        return null;
    }
}
