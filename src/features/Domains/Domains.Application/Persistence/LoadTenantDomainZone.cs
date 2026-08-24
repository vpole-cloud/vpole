using MongoDB.Driver;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.ResultType;

namespace Pole.Features.Domains.Application.Persistence;

public delegate Task<Result<TenantDomainZoneSnapshot, DomainZoneNotFound>>
    LoadTenantDomainZone(
        string tenantId,
        string zoneId,
        CancellationToken cancellationToken = default);

internal sealed class MongoLoadTenantDomainZone(IMongoDatabase database)
{
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);

    public async Task<Result<TenantDomainZoneSnapshot, DomainZoneNotFound>> InvokeAsync(
        string tenantId,
        string zoneId,
        CancellationToken cancellationToken = default)
    {
        var zone = await zones.Find(DomainFilters.ZoneById(tenantId, zoneId))
            .FirstOrDefaultAsync(cancellationToken);
        return zone is null
            ? new Err<DomainZoneNotFound>(new DomainZoneNotFound(zoneId))
            : new Ok<TenantDomainZoneSnapshot>(zone.ToSnapshot());
    }
}
