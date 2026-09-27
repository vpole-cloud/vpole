using MongoDB.Driver;
using Pole.Features.Tenants.Contracts;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Persistence.Identity;

internal sealed class MongoLoadTenantIdentity(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

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
