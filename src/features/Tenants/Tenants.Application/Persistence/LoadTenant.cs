using MongoDB.Driver;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Persistence;

public delegate Task<Tenant?> LoadTenant(
    string tenantId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadTenant(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public async Task<Tenant?> InvokeAsync(
        string tenantId,
        CancellationToken cancellationToken = default) =>
        await _tenants
            .Find(TenantFilters.ById(tenantId))
            .FirstOrDefaultAsync(cancellationToken);
}
