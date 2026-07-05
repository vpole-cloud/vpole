using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Domain;

namespace Pole.Features.Tenants.Application.Common;

public interface ITenantDbContext : IMongoDbContext
{
    IMongoCollection<Tenant> Tenants { get; }
    IMongoCollection<PoleUser> Users { get; }
}

