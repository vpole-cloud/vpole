using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Common;

public interface ITenantDbContext : IMongoDbContext
{
    IMongoCollection<Tenant> Tenants { get; }
}

