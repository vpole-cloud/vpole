using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Application.Lifecycle;
using Pole.Features.Domains.Application.HostnameBindings;
using Pole.Features.Domains.Application;
using MongoDB.Driver;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.DomainZones;

internal sealed class MongoDomainZoneReferences(IMongoCollection<TenantDomainZone> zones)
{
    public async Task<NamedDomainZoneOutcome> ResolveAsync(
        NamedDomainZoneRequest request,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var filter = DomainFilters.ZoneByTechnicalName(request.TenantId, request.TechnicalName);
        var zone = await (session is null ? zones.Find(filter) : zones.Find(session, filter))
            .FirstOrDefaultAsync(cancellationToken);
        return zone is null
            ? new NamedDomainZoneNotFound(request.TechnicalName)
            : new NamedDomainZoneFound(zone.Id);
    }

    public async Task<DomainZonePlanningSnapshot?> LoadAsync(
        LoadDomainZonePlanningRequest request,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var filter = DomainFilters.ZoneById(request.TenantId, request.Id);
        var zone = await (session is null ? zones.Find(filter) : zones.Find(session, filter))
            .FirstOrDefaultAsync(cancellationToken);
        return zone is null ? null : new DomainZonePlanningSnapshot(zone.ToSnapshot(), zone.ReferenceFence);
    }

    public async Task<FenceDomainZoneOutcome> FenceAsync(
        FenceDomainZoneRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        DomainTransactions.EnsureActive(session);
        var filter = DomainFilters.ZoneAtPlanningVersion(request);
        var updated = await zones.UpdateOneAsync(
            session,
            filter,
            Builders<TenantDomainZone>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        return updated.ModifiedCount == 1
            ? new DomainZoneFenced()
            : new DomainZonePreconditionChanged(request.Id);
    }
}
