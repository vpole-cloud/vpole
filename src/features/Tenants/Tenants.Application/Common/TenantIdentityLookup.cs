using MongoDB.Driver;
using Pole.Features.Tenants.Contracts;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Common;

public sealed class TenantIdentityLookup(ITenantDbContext dbContext) : ITenantIdentityLookup
{
    public async Task<Result<TenantIdentitySnapshot>> GetAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var tenant = await dbContext.Tenants
            .Find(TenantFilters.ById(tenantId))
            .FirstOrDefaultAsync(cancellationToken);
        return tenant is null
            ? Failures.NotFound("Tenant not found.", tenantId)
            : new TenantIdentitySnapshot(tenant.Id, tenant.Name);
    }
}
