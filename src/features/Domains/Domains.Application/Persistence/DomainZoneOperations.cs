using MongoDB.Driver;
using Pole.Features.Domains.Contracts;
using Pole.ResultType;

namespace Pole.Features.Domains.Application.Persistence;

public sealed record DomainZoneNotFound(string ZoneId);

public delegate Task<Result<TenantDomainZoneSnapshot, DomainZoneNotFound>>
    LoadTenantDomainZone(
        string tenantId,
        string zoneId,
        CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<TenantDomainZoneSnapshot>> LoadTenantDomainZones(
    string tenantId,
    string? deploymentTargetId = null,
    CancellationToken cancellationToken = default);

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

public sealed record ManagedDomainZoneDeleted(string ZoneId);

public sealed record GeneratedDomainZoneDeletionRejected(string ZoneId);

public sealed record DomainZoneHasHostnameBindings(
    string ZoneId,
    long BindingCount);

public sealed record DomainZoneDeletionConflict(string ZoneId);

public sealed record ManagedDomainZoneReferenceRemovalRejected(
    string ZoneId,
    string DeploymentTargetId);

public union DeleteManagedDomainZoneError(
    DomainZoneNotFound,
    GeneratedDomainZoneDeletionRejected,
    DomainZoneHasHostnameBindings,
    DomainZoneDeletionConflict,
    ManagedDomainZoneReferenceRemovalRejected);

public delegate Task<Result<TenantDomainZoneSnapshot, CreateManagedDomainZoneError>>
    CreateManagedDomainZoneReservation(
        string tenantId,
        string deploymentTargetId,
        string label,
        string publicBaseDomain,
        long expectedWorkloadConfigurationRevision,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<Result<ManagedDomainZoneDeleted, DeleteManagedDomainZoneError>>
    DeleteManagedDomainZone(
        string tenantId,
        string zoneId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
