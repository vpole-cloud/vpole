using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Domain;
using Pole.ResultType;

namespace Pole.Features.Domains.Application.Persistence.DomainZones;

public sealed record ManagedDomainZoneDeleted(string ZoneId);

public sealed record GeneratedDomainZoneDeletionRejected(string ZoneId);

public sealed record DomainZoneHasHostnameBindings(
    string ZoneId,
    long BindingCount);

public sealed record DomainZoneDeletionConflict(string ZoneId);

public sealed record ManagedDomainZoneReferenceRemovalRejected(
    string ZoneId,
    string DeploymentTargetId);

public union DeleteManagedDomainZoneOutcome(
    ManagedDomainZoneDeleted,
    DomainZoneNotFound,
    GeneratedDomainZoneDeletionRejected,
    DomainZoneHasHostnameBindings,
    DomainZoneDeletionConflict,
    ManagedDomainZoneReferenceRemovalRejected);

public delegate Task<DeleteManagedDomainZoneOutcome>
    DeleteManagedDomainZone(
        string tenantId,
        string zoneId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

internal sealed class MongoDeleteManagedDomainZone(
    IMongoDatabase database,
    ApplyDeploymentTargetReferenceChanges applyDeploymentTargetReferenceChanges)
{
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);
    private readonly IMongoCollection<HostnameBinding> bindings =
        database.GetCollection<HostnameBinding>(DomainCollectionNames.HostnameBindings);

    public async Task<DeleteManagedDomainZoneOutcome>
        InvokeAsync(
            string tenantId,
            string zoneId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        var zone = await zones.Find(
                session,
                DomainFilters.ZoneById(tenantId, zoneId))
            .FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
        {
            return new DomainZoneNotFound(zoneId);
        }
        if (zone.Kind == TenantDomainZoneKind.Generated)
        {
            return new GeneratedDomainZoneDeletionRejected(zoneId);
        }
        var bindingCount = await bindings.CountDocumentsAsync(
            session,
            DomainFilters.BindingsForZone(zoneId),
            cancellationToken: cancellationToken);
        if (bindingCount > 0)
        {
            return new DomainZoneHasHostnameBindings(zoneId, bindingCount);
        }
        var deleted = await zones.DeleteOneAsync(
            session,
            DomainFilters.ZoneAtRevision(tenantId, zone),
            cancellationToken: cancellationToken);
        if (deleted.DeletedCount != 1)
        {
            return new DomainZoneDeletionConflict(zoneId);
        }

        var unregistered = await applyDeploymentTargetReferenceChanges(
            new DeploymentTargetReferenceOwner(
                DeploymentTargetReferenceOwnerTypes.DomainZone,
                zone.Id),
            [],
            [zone.DeploymentTargetId],
            session,
            cancellationToken);
        return unregistered is Ok<Unit>
            ? new ManagedDomainZoneDeleted(zone.Id)
            : new ManagedDomainZoneReferenceRemovalRejected(
                    zone.Id,
                    zone.DeploymentTargetId);
    }
}
