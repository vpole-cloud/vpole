using MongoDB.Driver;
using System.Collections.Immutable;
using Pole.Features.Core.Multitenancy;
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

    public async Task<string> CreateOwnedAsync(string userId, string email, string name = "Test tenant", string? id = null)
    {
        var tenant = TenantFactory.Create(name, id);
        tenant = tenant with
        {
            Members = ImmutableList.Create(TenantFactory.CreateMember(tenant.Id, userId, email, TenantRole.Owner))
        };
        await database.GetCollection<Tenant>("Tenants").InsertOneAsync(tenant);
        return tenant.Id;
    }
}
