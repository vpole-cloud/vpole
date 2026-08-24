using MongoDB.Driver;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Persistence;

public delegate Task<IReadOnlyList<Tenant>> LoadTenantsForUser(
    string userId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadTenantsForUser(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public async Task<IReadOnlyList<Tenant>> InvokeAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        await _tenants
            .Find(TenantFilters.ForMemberUser(userId))
            .ToListAsync(cancellationToken);
}
