using MongoDB.Driver;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Persistence;

public delegate Task EnsureDefaultTenant(
    Tenant tenant,
    CancellationToken cancellationToken = default);

internal sealed class MongoEnsureDefaultTenant(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public Task InvokeAsync(
        Tenant tenant,
        CancellationToken cancellationToken = default) =>
        _tenants.UpdateOneAsync(
            TenantFilters.ById(tenant.Id),
            TenantUpdates.CreateIfMissing(tenant),
            new UpdateOptions { IsUpsert = true },
            cancellationToken);
}
