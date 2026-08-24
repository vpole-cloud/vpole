using MongoDB.Driver;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Persistence;

public delegate Task PersistCreatedTenant(
    Tenant tenant,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedTenant(IMongoDatabase database)
{
    private readonly IMongoCollection<Tenant> _tenants = database.GetCollection<Tenant>("Tenants");

    public Task InvokeAsync(
        Tenant tenant,
        CancellationToken cancellationToken = default) =>
        _tenants.InsertOneAsync(tenant, cancellationToken: cancellationToken);
}
