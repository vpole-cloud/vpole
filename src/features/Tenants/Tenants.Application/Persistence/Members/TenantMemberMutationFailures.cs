using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Persistence.Members;

internal static class TenantMemberMutationFailures
{
    public static async Task<Err<TenantMemberMutationError>> ResolveAsync(
        IMongoCollection<Tenant> tenants,
        string tenantId,
        string memberId,
        CancellationToken cancellationToken)
    {
        var tenant = await tenants
            .Find(TenantFilters.ById(tenantId))
            .FirstOrDefaultAsync(cancellationToken);
        var member = tenant?.Members.FirstOrDefault(candidate => candidate.Id == memberId);
        if (tenant is null)
        {
            return new Err<TenantMemberMutationError>(
                new TenantMemberMutationError(new TenantNotFound(tenantId)));
        }

        if (member is not null &&
            member.Role == TenantRole.Owner &&
            tenant.Members.Count(candidate => candidate.Role == TenantRole.Owner) == 1)
        {
            return new Err<TenantMemberMutationError>(
                new TenantMemberMutationError(new TenantLastOwner(tenantId, memberId)));
        }

        return new Err<TenantMemberMutationError>(
            new TenantMemberMutationError(new TenantMemberNotFound(tenantId, memberId)));
    }
}
