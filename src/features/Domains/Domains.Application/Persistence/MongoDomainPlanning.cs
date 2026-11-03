using MongoDB.Driver;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.Persistence;

internal sealed class MongoDomainPlanning(
    IMongoCollection<TenantDomainZone> zones,
    IMongoCollection<HostnameBinding> bindings)
{
    public async Task<NamedDomainZoneOutcome> ResolveZoneAsync(
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

    public async Task<NamedHostnameBindingOutcome> ResolveBindingAsync(
        NamedHostnameBindingRequest request,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var filter = DomainFilters.BindingByTechnicalName(request.TenantId, request.TechnicalName);
        var binding = await (session is null ? bindings.Find(filter) : bindings.Find(session, filter))
            .FirstOrDefaultAsync(cancellationToken);
        return binding is null
            ? new NamedHostnameBindingNotFound(request.TechnicalName)
            : new NamedHostnameBindingFound(binding.Id);
    }

    public async Task<DomainZonePlanningSnapshot?> LoadZoneAsync(
        LoadDomainZonePlanningRequest request,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var filter = DomainFilters.ZoneById(request.TenantId, request.Id);
        var zone = await (session is null ? zones.Find(filter) : zones.Find(session, filter))
            .FirstOrDefaultAsync(cancellationToken);
        return zone is null ? null : new DomainZonePlanningSnapshot(zone.ToSnapshot(), zone.ReferenceFence);
    }

    public async Task<HostnameBindingPlanningSnapshot?> LoadBindingAsync(
        LoadHostnameBindingPlanningRequest request,
        IClientSessionHandle? session,
        CancellationToken cancellationToken)
    {
        var filter = DomainFilters.BindingById(request.TenantId, request.Id);
        var binding = await (session is null ? bindings.Find(filter) : bindings.Find(session, filter))
            .FirstOrDefaultAsync(cancellationToken);
        return binding is null
            ? null
            : new HostnameBindingPlanningSnapshot(binding.ToSnapshot(), binding.ReferenceFence);
    }

    public async Task<FenceDomainZoneOutcome> FenceZoneAsync(
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

    public async Task<FenceHostnameBindingOutcome> FenceBindingAsync(
        FenceHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        DomainTransactions.EnsureActive(session);
        var filter = DomainFilters.BindingAtPlanningVersion(request);
        var updated = await bindings.UpdateOneAsync(
            session,
            filter,
            Builders<HostnameBinding>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        return updated.ModifiedCount == 1
            ? new HostnameBindingFenced()
            : new HostnameBindingPreconditionChanged(request.Id);
    }
}
