using MongoDB.Driver;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Application.Tenants;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Members;

public delegate Task<IReadOnlyList<Tenant>> LoadTenantsForUser(
    string userId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadTenantsForUser(IMongoCollection<Tenant> _tenants)
{
    public async Task<IReadOnlyList<Tenant>> InvokeAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        await _tenants
            .Find(TenantFilters.ForMemberUser(userId))
            .ToListAsync(cancellationToken);
}
