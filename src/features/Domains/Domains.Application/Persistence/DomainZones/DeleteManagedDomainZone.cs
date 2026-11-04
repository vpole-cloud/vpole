using MongoDB.Driver;
using Pole.Features.Outbox.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Domain;
using Pole.ResultType;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.Domains.Application.Persistence.DomainZones;

public sealed record ManagedDomainZoneDeleted(string ZoneId);

public sealed record GeneratedDomainZoneDeletionRejected(string ZoneId);

public sealed record DomainZoneHasHostnameBindings(
    string ZoneId,
    long BindingCount);

public sealed record DomainZoneDeletionConflict(string ZoneId);

public sealed record DomainZoneGitOwned(string ZoneId);

public sealed record ManagedDomainZoneReferenceRemovalRejected(
    string ZoneId,
    string DeploymentTargetId);

public union DeleteManagedDomainZoneOutcome(
    ManagedDomainZoneDeleted,
    DomainZoneNotFound,
    GeneratedDomainZoneDeletionRejected,
    DomainZoneHasHostnameBindings,
    DomainZoneDeletionConflict,
    DomainZoneGitOwned,
    ManagedDomainZoneReferenceRemovalRejected);

public sealed record RemoveManagedDomainZoneRequest(
    string TenantId,
    string ZoneId,
    GitLifecycleAuthority? Lifecycle = null);

public delegate Task<DeleteManagedDomainZoneOutcome>
    DeleteManagedDomainZone(
        string tenantId,
        string zoneId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

internal sealed class MongoDeleteManagedDomainZone(
    IMongoDatabase database,
    ApplyDeploymentTargetReferenceChanges applyDeploymentTargetReferenceChanges,
    StoreOutboxWrites outbox,
    IGitOpsMutations git)
{
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);

    private readonly IMongoCollection<HostnameBinding> bindings =
        database.GetCollection<HostnameBinding>(DomainCollectionNames.HostnameBindings);

    public Task<DeleteManagedDomainZoneOutcome> InvokeAsync(
        string tenantId,
        string zoneId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(new(tenantId, zoneId), session, cancellationToken);

    internal async Task<DeleteManagedDomainZoneOutcome> ExecuteAsync(
        RemoveManagedDomainZoneRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        DomainTransactions.EnsureActive(session);
        var zone = await zones.Find(
                session,
                DomainFilters.ZoneById(request.TenantId, request.ZoneId))
            .FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
        {
            return new DomainZoneNotFound(request.ZoneId);
        }

        if (zone.Kind == TenantDomainZoneKind.Generated)
        {
            return new GeneratedDomainZoneDeletionRejected(request.ZoneId);
        }

        if (!await git.AuthorizeAsync(new(request.TenantId, "DomainZone", request.ZoneId, Lifecycle: request.Lifecycle,
                    LifecycleEffect: GitLifecycleMutationEffect.Remove), session, cancellationToken))
            return new DomainZoneGitOwned(request.ZoneId);

        var bindingCount = await bindings.CountDocumentsAsync(
            session,
            DomainFilters.BindingsForTenantZone(request.TenantId, request.ZoneId),
            cancellationToken: cancellationToken);
        if (bindingCount > 0)
        {
            return new DomainZoneHasHostnameBindings(request.ZoneId, bindingCount);
        }

        var deleted = await zones.DeleteOneAsync(
            session,
            DomainFilters.ZoneAtRevision(request.TenantId, zone),
            cancellationToken: cancellationToken);
        if (deleted.DeletedCount != 1)
        {
            return new DomainZoneDeletionConflict(request.ZoneId);
        }

        var unregistered = await applyDeploymentTargetReferenceChanges(
            new ApplyDeploymentTargetReferenceChangesRequest(
                Owner: new DeploymentTargetReferenceOwner(
                DeploymentTargetReferenceOwnerTypes.DomainZone,
                zone.Id),
                AddedDeploymentTargetIds: [],
                RemovedDeploymentTargetIds: [zone.DeploymentTargetId]),
            session: session,
            cancellationToken: cancellationToken);
        if (unregistered is not DeploymentTargetReferencesChanged)
            return new ManagedDomainZoneReferenceRemovalRejected(zone.Id, zone.DeploymentTargetId);
        await outbox(session,
            [OutboxWrite.Create((zone with { Revision = checked(zone.Revision + 1) }).ToChangedEvent())],
            cancellationToken);
        return new ManagedDomainZoneDeleted(zone.Id);
    }
}
