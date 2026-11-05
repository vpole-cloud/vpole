using MongoDB.Driver;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Tenants;

public delegate Task<Tenant?> LoadTenant(
    string tenantId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadTenant(IMongoCollection<Tenant> _tenants)
{
    public async Task<Tenant?> InvokeAsync(
        string tenantId,
        CancellationToken cancellationToken = default) =>
        await _tenants
            .Find(TenantFilters.ById(tenantId))
            .FirstOrDefaultAsync(cancellationToken);
}
