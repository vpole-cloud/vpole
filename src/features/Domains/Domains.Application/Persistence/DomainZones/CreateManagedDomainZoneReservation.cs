using Pole.Database;
using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.Outbox.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.ResultType;

namespace Pole.Features.Domains.Application.Persistence.DomainZones;

public sealed record ManagedDomainZoneAlreadyReserved(string Domain);

public sealed record ManagedDomainZoneNameConflict(string TechnicalName);

public sealed record CreateManagedDomainZoneCommand(
    string Id,
    string TenantId,
    string DeploymentTargetId,
    string TechnicalName,
    string Label,
    string PublicBaseDomain,
    long ExpectedWorkloadConfigurationRevision);

public sealed record ManagedDomainZoneDeploymentTargetChanged(
    string DeploymentTargetId);

public sealed record ManagedDomainZoneReferenceRejected(
    string DeploymentTargetId);

public union CreateManagedDomainZoneOutcome(
    TenantDomainZoneSnapshot,
    ManagedDomainZoneAlreadyReserved,
    ManagedDomainZoneNameConflict,
    ManagedDomainZoneDeploymentTargetChanged,
    ManagedDomainZoneReferenceRejected);

public delegate Task<CreateManagedDomainZoneOutcome>
    CreateManagedDomainZoneReservation(
        CreateManagedDomainZoneCommand request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

internal sealed class MongoCreateManagedDomainZoneReservation(
    IMongoDatabase database,
    ApplyDeploymentTargetReferenceChanges applyDeploymentTargetReferenceChanges,
    FenceDeploymentTargetWorkloadConfigurations fenceDeploymentTargetWorkloadConfigurations,
    TimeProvider timeProvider,
    StoreOutboxWrites outbox)
{
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);

    public async Task<CreateManagedDomainZoneOutcome>
        InvokeAsync(
            CreateManagedDomainZoneCommand request,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        var zone = TenantDomainZoneFactory.CreateManagedReservation(
            request.TenantId,
            request.DeploymentTargetId,
            request.Label,
            DnsName.Create(request.PublicBaseDomain),
            request.TechnicalName,
            new TenantDomainZoneFactory.CreateOptions(
                request.Id,
                timeProvider.GetUtcNow().UtcDateTime));

        var fence = await fenceDeploymentTargetWorkloadConfigurations(
            [
                new DeploymentTargetWorkloadConfigurationFenceRequest(
                    request.DeploymentTargetId,
                    request.ExpectedWorkloadConfigurationRevision)
            ],
            session,
            cancellationToken);
        if (fence is DeploymentTargetWorkloadConfigurationFenceRejected)
        {
            return new ManagedDomainZoneDeploymentTargetChanged(request.DeploymentTargetId);
        }

        try
        {
            await zones.InsertOneAsync(session, zone, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
            exception, DomainIndexNames.ZoneTechnicalName, zones.CollectionNamespace.CollectionName))
        {
            return new ManagedDomainZoneNameConflict(request.TechnicalName);
        }
        catch (MongoWriteException exception) when (MongoDuplicateKey.IsForIndex(
            exception, DomainIndexNames.ZoneTargetDomain, zones.CollectionNamespace.CollectionName))
        {
            return new ManagedDomainZoneAlreadyReserved(zone.Domain);
        }

        var registered = await applyDeploymentTargetReferenceChanges(
            new ApplyDeploymentTargetReferenceChangesRequest(
                Owner: new DeploymentTargetReferenceOwner(
                DeploymentTargetReferenceOwnerTypes.DomainZone,
                zone.Id),
                AddedDeploymentTargetIds: [zone.DeploymentTargetId],
                RemovedDeploymentTargetIds: []),
            session: session,
            cancellationToken: cancellationToken);
        if (registered is not DeploymentTargetReferencesChanged)
            return new ManagedDomainZoneReferenceRejected(request.DeploymentTargetId);
        await outbox(session, [OutboxWrite.Create(zone.ToChangedEvent())], cancellationToken);
        return zone.ToSnapshot();
    }
}
