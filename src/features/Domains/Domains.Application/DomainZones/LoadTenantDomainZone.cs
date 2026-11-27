// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.DomainZones;

public union LoadTenantDomainZoneOutcome(TenantDomainZoneSnapshot, DomainZoneNotFound);

public delegate Task<LoadTenantDomainZoneOutcome> LoadTenantDomainZone(
    string tenantId,
    string zoneId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadTenantDomainZone(
    IMongoCollection<TenantDomainZone> zones
)
{
    public async Task<LoadTenantDomainZoneOutcome> InvokeAsync(
        string tenantId,
        string zoneId,
        CancellationToken cancellationToken = default)
    {
        var zone = await zones.Find(DomainFilters.ZoneById(tenantId, zoneId)).FirstOrDefaultAsync(cancellationToken);
        return zone is null ? new DomainZoneNotFound(zoneId) : zone.ToSnapshot();
    }
}
