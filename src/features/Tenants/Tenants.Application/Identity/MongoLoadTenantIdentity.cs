using MongoDB.Driver;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Application.Tenants;
using Pole.Features.Tenants.Contracts;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Identity;

internal sealed class MongoLoadTenantIdentity(IMongoCollection<Tenant> _tenants)
{
    public async Task<LoadTenantIdentityOutcome> InvokeAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var tenant = await _tenants
            .Find(TenantFilters.ById(tenantId))
            .FirstOrDefaultAsync(cancellationToken);
        return tenant is null
            ? new TenantIdentityNotFound(tenantId)
            : new TenantIdentitySnapshot(tenant.Id, tenant.Name);
    }
}
