using MongoDB.Driver;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.Persistence.DomainZones;

public delegate Task<IReadOnlyList<TenantDomainZoneSnapshot>> LoadTenantDomainZones(
    string tenantId,
    string? deploymentTargetId = null,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadTenantDomainZones(IMongoDatabase database)
{
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);

    public async Task<IReadOnlyList<TenantDomainZoneSnapshot>> InvokeAsync(
        string tenantId,
        string? deploymentTargetId = null,
        CancellationToken cancellationToken = default)
    {
        var snapshots = await zones.Find(
                DomainFilters.ZonesForTenant(tenantId, deploymentTargetId))
            .SortBy(zone => zone.DeploymentTargetId)
            .ThenBy(zone => zone.Domain)
            .ThenBy(zone => zone.Id)
            .ToListAsync(cancellationToken);
        return snapshots.Select(DomainContractMappings.ToSnapshot).ToArray();
    }
}
