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

/// <summary>
/// Transactional domain-zone operations. Consumers pass their active Mongo session so zone
/// reservation and the caller's desired-state mutation commit or roll back together.
/// </summary>
public interface ITenantDomainZoneCapability
{
    Task<Result<TenantDomainZoneSnapshot>> GetAsync(
        string tenantId,
        string zoneId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantDomainZoneSnapshot>> ListAsync(
        string tenantId,
        string? deploymentTargetId = null,
        CancellationToken cancellationToken = default);

    Task<Result<TenantDomainZoneSnapshot>> GetOrCreateGeneratedAsync(
        GetOrCreateGeneratedZoneRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets or creates generated zones for one tenant and a unique ordered target set.
    /// Results preserve request order and all persistence joins the caller's transaction.
    /// </summary>
    Task<Result<IReadOnlyList<TenantDomainZoneSnapshot>>> GetOrCreateGeneratedSetAsync(
        IReadOnlyList<GetOrCreateGeneratedZoneRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result<TenantDomainZoneSnapshot>> CreateManagedReservationAsync(
        string tenantId,
        string deploymentTargetId,
        string label,
        string publicBaseDomain,
        long expectedWorkloadConfigurationRevision,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteManagedReservationAsync(
        string tenantId,
        string zoneId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

/// <summary>Hostname ownership capability for ResourceDeployment-aware callers.</summary>
public interface IHostnameBindingCapability
{
    Task<IReadOnlyList<HostnameBindingSnapshot>> ListForDeploymentAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        CancellationToken cancellationToken = default);

    Task<Result<HostnameBindingSnapshot>> CreateGeneratedAsync(
        CreateGeneratedHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates one generated binding for every ordered deployment request. Implementations
    /// preload zones and conflicts as sets and keep every write in the caller's transaction.
    /// </summary>
    Task<Result<IReadOnlyList<HostnameBindingSnapshot>>> CreateGeneratedSetAsync(
        IReadOnlyList<CreateGeneratedHostnameBindingRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result<HostnameBindingSnapshot>> CreateCustomAsync(
        CreateCustomHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteCustomAsync(
        DeleteCustomHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all bindings only after the exact physical deployment has reached terminal
    /// deletion. This is deliberately not a tenant-wide or logical-resource purge.
    /// </summary>
    Task<Result> DeleteAllForDeploymentAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteAllForResourceAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListHostnamesAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HostnameBindingSnapshot>> ListForDeploymentInTransactionAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}
