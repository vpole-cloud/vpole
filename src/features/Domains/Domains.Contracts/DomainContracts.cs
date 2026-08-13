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

public delegate Task<Result<TenantDomainZoneSnapshot>> GetOrCreateGeneratedDomainZone(
    GetOrCreateGeneratedZoneRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<Result<IReadOnlyList<TenantDomainZoneSnapshot>>>
    GetOrCreateGeneratedDomainZones(
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

public delegate Task<Result<HostnameBindingSnapshot>> CreateGeneratedHostnameBinding(
    CreateGeneratedHostnameBindingRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<Result<IReadOnlyList<HostnameBindingSnapshot>>>
    CreateGeneratedHostnameBindings(
        IReadOnlyList<CreateGeneratedHostnameBindingRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<Result<HostnameBindingSnapshot>> CreateCustomHostnameBinding(
    CreateCustomHostnameBindingRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<Result> DeleteCustomHostnameBinding(
    DeleteCustomHostnameBindingRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<Result> DeleteHostnameBindingsForDeployment(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    string resourceDeploymentId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<Result> DeleteHostnameBindingsForResource(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
