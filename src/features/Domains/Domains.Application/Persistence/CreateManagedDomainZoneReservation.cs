using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.ResultType;

namespace Pole.Features.Domains.Application.Persistence;

public sealed record InvalidManagedDomainZone(string Message);

public sealed record ManagedDomainZoneAlreadyReserved(string Domain);

public sealed record ManagedDomainZoneDeploymentTargetChanged(
    string DeploymentTargetId);

public sealed record ManagedDomainZoneReferenceRejected(
    string DeploymentTargetId);

public union CreateManagedDomainZoneError(
    InvalidManagedDomainZone,
    ManagedDomainZoneAlreadyReserved,
    ManagedDomainZoneDeploymentTargetChanged,
    ManagedDomainZoneReferenceRejected);

public delegate Task<Result<TenantDomainZoneSnapshot, CreateManagedDomainZoneError>>
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

    public async Task<Result<TenantDomainZoneSnapshot, CreateManagedDomainZoneError>>
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
        TenantDomainZone zone;
        try
        {
            zone = TenantDomainZoneFactory.CreateManagedReservation(
                tenantId,
                deploymentTargetId,
                label,
                DnsName.Create(publicBaseDomain),
                new TenantDomainZoneFactory.CreateOptions(
                    ObjectId.GenerateNewId().ToString(),
                    timeProvider.GetUtcNow().UtcDateTime));
        }
        catch (ArgumentException exception)
        {
            return new Err<CreateManagedDomainZoneError>(
                new InvalidManagedDomainZone(exception.Message));
        }

        var fence = await fenceDeploymentTargetWorkloadConfigurations(
            [new DeploymentTargetWorkloadConfigurationFenceRequest(
                deploymentTargetId,
                expectedWorkloadConfigurationRevision)],
            session,
            cancellationToken);
        if (fence is Err<DeploymentTargetWorkloadConfigurationFenceRejected>)
        {
            return new Err<CreateManagedDomainZoneError>(
                new ManagedDomainZoneDeploymentTargetChanged(deploymentTargetId));
        }

        try
        {
            await zones.InsertOneAsync(session, zone, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new Err<CreateManagedDomainZoneError>(
                new ManagedDomainZoneAlreadyReserved(zone.Domain));
        }

        var registered = await applyDeploymentTargetReferenceChanges(
            new DeploymentTargetReferenceOwner(
                DeploymentTargetReferenceOwnerTypes.DomainZone,
                zone.Id),
            [zone.DeploymentTargetId],
            [],
            session,
            cancellationToken);
        return registered is Ok<Unit>
            ? new Ok<TenantDomainZoneSnapshot>(zone.ToSnapshot())
            : new Err<CreateManagedDomainZoneError>(
                new ManagedDomainZoneReferenceRejected(deploymentTargetId));
    }
}
