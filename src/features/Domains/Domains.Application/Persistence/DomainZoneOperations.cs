using MongoDB.Driver;
using Pole.Features.Domains.Contracts;
using Pole.ResultType;

namespace Pole.Features.Domains.Application.Persistence;

public delegate Task<Result<TenantDomainZoneSnapshot>> LoadTenantDomainZone(
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

public delegate Task<Result<TenantDomainZoneSnapshot, CreateManagedDomainZoneError>>
    CreateManagedDomainZoneReservation(
        string tenantId,
        string deploymentTargetId,
        string label,
        string publicBaseDomain,
        long expectedWorkloadConfigurationRevision,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<Result> DeleteManagedDomainZone(
    string tenantId,
    string zoneId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
