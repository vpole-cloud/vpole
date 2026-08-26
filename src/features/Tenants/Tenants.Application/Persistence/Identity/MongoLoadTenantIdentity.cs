using MongoDB.Driver;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Contracts;
using Pole.Features.Tenants.Domain;
using Pole.ResultType;

namespace Pole.Features.Tenants.Application.Persistence;

internal sealed class MongoLoadTenantIdentity(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public async Task<Result<TenantIdentitySnapshot, TenantIdentityNotFound>> InvokeAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var tenant = await _tenants
            .Find(TenantFilters.ById(tenantId))
            .FirstOrDefaultAsync(cancellationToken);
        return tenant is null
            ? new Err<TenantIdentityNotFound>(new TenantIdentityNotFound(tenantId))
            : new Ok<TenantIdentitySnapshot>(
                new TenantIdentitySnapshot(tenant.Id, tenant.Name));
    }
}
