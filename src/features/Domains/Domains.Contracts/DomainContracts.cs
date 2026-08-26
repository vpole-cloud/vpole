using MongoDB.Driver;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.Domains.Contracts;

public static class DomainZoneKinds
{
    public const string Generated = "generated";
    public const string ManagedReservation = "managed-reservation";
}

public static class HostnameBindingKinds
{
    public const string Generated = "generated";
    public const string Custom = "custom";
}

public static class DomainCapabilityLimits
{
    public const int MaximumSetSize = 64;
}

public sealed record TenantDomainZoneSnapshot(
    string Id,
    string TenantId,
    string DeploymentTargetId,
    string Label,
    string Domain,
    string Kind,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record HostnameBindingSnapshot(
    string Id,
    string TenantId,
    string TenantDomainZoneId,
    string DeploymentTargetId,
    string ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string RelativeName,
    string Hostname,
    string Kind,
    DateTime CreatedAtUtc);

public sealed record GetOrCreateGeneratedZoneRequest(
    string TenantId,
    string DeploymentTargetId,
    string PublicBaseDomain,
    long ExpectedWorkloadConfigurationRevision);

public sealed record CreateGeneratedHostnameBindingRequest(
    string TenantId,
    string TenantDomainZoneId,
    string DeploymentTargetId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string RelativeName);

public sealed record CreateCustomHostnameBindingRequest(
    string TenantId,
    string TenantDomainZoneId,
    string DeploymentTargetId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string RelativeName);

public sealed record DeleteCustomHostnameBindingRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string BindingId);

public sealed record CustomHostnameBindingZoneNotFound(string TenantDomainZoneId);

public sealed record CustomHostnameBindingTargetMismatch(
    string TenantDomainZoneId,
    string DeploymentTargetId);

public sealed record CustomHostnameBindingConflict(string Hostname);

public sealed record CustomHostnameBindingNotFound(string BindingId);

public sealed record GeneratedHostnameBindingDeletionRejected(string BindingId);

public sealed record CustomHostnameBindingZoneUnavailable(string TenantDomainZoneId);

public sealed record CustomHostnameBindingZoneChanged(
    IReadOnlyList<string> TenantDomainZoneIds);

public sealed record CustomHostnameBindingDeletionConflict(string BindingId);

public union CustomHostnameBindingError(
    CustomHostnameBindingZoneNotFound,
    CustomHostnameBindingTargetMismatch,
    CustomHostnameBindingConflict,
    CustomHostnameBindingNotFound,
    GeneratedHostnameBindingDeletionRejected,
    CustomHostnameBindingZoneUnavailable,
    CustomHostnameBindingZoneChanged,
    CustomHostnameBindingDeletionConflict);

public sealed record GeneratedDomainZoneTenantUnavailable(string TenantId);

public sealed record GeneratedDomainZoneTargetChanged(
    IReadOnlyList<string> DeploymentTargetIds);

public sealed record GeneratedDomainZoneMismatch(string DeploymentTargetId);

public sealed record GeneratedDomainZoneConflict(
    IReadOnlyList<string> DeploymentTargetIds);

public union GeneratedDomainZoneError(
    GeneratedDomainZoneTenantUnavailable,
    GeneratedDomainZoneTargetChanged,
    GeneratedDomainZoneMismatch,
    GeneratedDomainZoneConflict);

public sealed record GeneratedHostnameBindingZoneNotFound(string TenantDomainZoneId);

public sealed record GeneratedHostnameBindingTargetMismatch(
    string TenantDomainZoneId,
    string DeploymentTargetId);

public sealed record GeneratedHostnameBindingZoneChanged(
    IReadOnlyList<string> TenantDomainZoneIds);

public sealed record GeneratedHostnameBindingConflict(
    IReadOnlyList<string> Hostnames);

public union GeneratedHostnameBindingError(
    GeneratedHostnameBindingZoneNotFound,
    GeneratedHostnameBindingTargetMismatch,
    GeneratedHostnameBindingZoneChanged,
    GeneratedHostnameBindingConflict);

/// <summary>
/// Expected provider states while removing generated or custom hostname bindings as part of a
/// resource-deployment finalization. Consumers use this contract inside their own transaction;
/// it deliberately has no HTTP meaning.
/// </summary>
public sealed record HostnameBindingCleanupZoneUnavailable(
    IReadOnlyList<string> TenantDomainZoneIds);

public sealed record HostnameBindingCleanupZoneChanged(
    IReadOnlyList<string> TenantDomainZoneIds);

public sealed record HostnameBindingCleanupBindingsChanged;

public sealed record HostnameBindingCleanupReferenceRemovalRejected(
    string TenantDomainZoneId,
    string DeploymentTargetId);

public union HostnameBindingCleanupError(
    HostnameBindingCleanupZoneUnavailable,
    HostnameBindingCleanupZoneChanged,
    HostnameBindingCleanupBindingsChanged,
    HostnameBindingCleanupReferenceRemovalRejected);

public delegate Task<Result<TenantDomainZoneSnapshot, GeneratedDomainZoneError>>
    GetOrCreateGeneratedDomainZoneOutcome(
        GetOrCreateGeneratedZoneRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<Result<
    IReadOnlyList<TenantDomainZoneSnapshot>,
    GeneratedDomainZoneError>> GetOrCreateGeneratedDomainZonesOutcome(
        IReadOnlyList<GetOrCreateGeneratedZoneRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<HostnameBindingSnapshot>>
    LoadHostnameBindingsForDeployment(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<HostnameBindingSnapshot>>
    LoadHostnameBindingsForDeploymentInTransaction(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<Result<HostnameBindingSnapshot, GeneratedHostnameBindingError>>
    CreateGeneratedHostnameBindingOutcome(
        CreateGeneratedHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<Result<
    IReadOnlyList<HostnameBindingSnapshot>,
    GeneratedHostnameBindingError>> CreateGeneratedHostnameBindingsOutcome(
        IReadOnlyList<CreateGeneratedHostnameBindingRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<Result<HostnameBindingSnapshot, CustomHostnameBindingError>>
    CreateCustomHostnameBindingOutcome(
        CreateCustomHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<Result<Unit, CustomHostnameBindingError>>
    DeleteCustomHostnameBindingOutcome(
        DeleteCustomHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<Result<Unit, HostnameBindingCleanupError>>
    DeleteHostnameBindingsForDeployment(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    string resourceDeploymentId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<Result<Unit, HostnameBindingCleanupError>>
    DeleteHostnameBindingsForResource(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
