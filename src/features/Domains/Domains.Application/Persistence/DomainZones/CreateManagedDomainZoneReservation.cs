using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.ResultType;

namespace Pole.Features.Domains.Application.Persistence.DomainZones;

public sealed record ManagedDomainZoneAlreadyReserved(string Domain);

public sealed record ManagedDomainZoneDeploymentTargetChanged(
    string DeploymentTargetId);

public sealed record ManagedDomainZoneReferenceRejected(
    string DeploymentTargetId);

public union CreateManagedDomainZoneOutcome(
    TenantDomainZoneSnapshot,
    ManagedDomainZoneAlreadyReserved,
    ManagedDomainZoneDeploymentTargetChanged,
    ManagedDomainZoneReferenceRejected);

public delegate Task<CreateManagedDomainZoneOutcome>
    CreateManagedDomainZoneReservation(
        string tenantId,
        string deploymentTargetId,
        string label,
        string publicBaseDomain,
        long expectedWorkloadConfigurationRevision,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

internal sealed class MongoCreateManagedDomainZoneReservation(
    IMongoDatabase database,
    ApplyDeploymentTargetReferenceChanges applyDeploymentTargetReferenceChanges,
    FenceDeploymentTargetWorkloadConfigurations fenceDeploymentTargetWorkloadConfigurations,
    TimeProvider timeProvider)
{
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);

    public async Task<CreateManagedDomainZoneOutcome>
        InvokeAsync(
            string tenantId,
            string deploymentTargetId,
            string label,
            string publicBaseDomain,
            long expectedWorkloadConfigurationRevision,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        var zone = TenantDomainZoneFactory.CreateManagedReservation(
            tenantId,
            deploymentTargetId,
            label,
            DnsName.Create(publicBaseDomain),
            new TenantDomainZoneFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));

        var fence = await fenceDeploymentTargetWorkloadConfigurations(
            [new DeploymentTargetWorkloadConfigurationFenceRequest(
                deploymentTargetId,
                expectedWorkloadConfigurationRevision)],
            session,
            cancellationToken);
        if (fence is DeploymentTargetWorkloadConfigurationFenceRejected)
        {
            return new ManagedDomainZoneDeploymentTargetChanged(deploymentTargetId);
        }

        try
        {
            await zones.InsertOneAsync(session, zone, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new ManagedDomainZoneAlreadyReserved(zone.Domain);
        }

        var registered = await applyDeploymentTargetReferenceChanges(
            new DeploymentTargetReferenceOwner(
                DeploymentTargetReferenceOwnerTypes.DomainZone,
                zone.Id),
            [zone.DeploymentTargetId],
            [],
            session,
            cancellationToken);
        return registered is DeploymentTargetReferencesChanged
            ? zone.ToSnapshot()
            : new ManagedDomainZoneReferenceRejected(deploymentTargetId);
    }
}
