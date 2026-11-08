using MongoDB.Driver;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Testing;

public sealed class TenantTestData(IMongoDatabase database)
{
    public async Task<string> CreateAsync(string name = "Test tenant", string? id = null)
    {
        var tenant = TenantFactory.Create(name, id);
        await database.GetCollection<Tenant>("Tenants").InsertOneAsync(tenant);
        return tenant.Id;
    }
}
