using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record PlacementRequest(
    string? IsolationScopeId,
    string? DeploymentTargetId);

public sealed record ResourceDeploymentEndpointDto(
    string Kind,
    string Host,
    int Port,
    string Scheme)
{
    public static ResourceDeploymentEndpointDto Create(
        string kind,
        string host,
        int port,
        string scheme)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Endpoint kind is required.", nameof(kind));
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("Endpoint host is required.", nameof(host));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Endpoint port is invalid.");
        if (string.IsNullOrWhiteSpace(scheme))
            throw new ArgumentException("Endpoint scheme is required.", nameof(scheme));

        return new ResourceDeploymentEndpointDto(
            kind.Trim(),
            host.Trim(),
            port,
            scheme.Trim().ToLowerInvariant());
    }
}

public sealed record ComputeProfileReference(
    string ComputeProfileId,
    long ComputeProfileRevision);

public sealed record ComputeResourceValuesDto(
    long CpuMillis,
    long MemoryMi);

public sealed record ComputeExtendedResourceDto(
    string Name,
    long Quantity);

public sealed record ComputeConfigurationSummaryDto(
    ComputeProfileReference Profile,
    string DisplayName,
    ComputeResourceValuesDto Requests,
    ComputeResourceValuesDto Limits,
    ImmutableList<ComputeExtendedResourceDto> ExtendedResources);

public sealed record CurrentResourceDeploymentComputeDto(
    string DeploymentTargetId,
    ComputeConfigurationSummaryDto Compute);

public sealed record PlacementSummaryDto(
    string DeploymentId,
    string IsolationScopeId,
    string RuntimeCellId,
    string DeploymentTargetId,
    long DesiredGeneration,
    long ObservedGeneration,
    string Status,
    string? FailureCode,
    ComputeConfigurationSummaryDto Compute,
    ImmutableList<ResourceDeploymentEndpointDto> Endpoints,
    long ResourceRevision = 1,
    int FixedReplicas = 1,
    ResourceDeploymentIngressConfiguration? Ingress = null,
    long DeploymentRevision = 1,
    string DesiredOperation = "Apply");

public sealed record ResourceDeploymentDto(
    string Id,
    string ResourceType,
    string ResourceId,
    string ResourceGroupId,
    PlacementSummaryDto Placement,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public interface IResourceDeploymentReferenceLookup
{
    Task<Result> EnsureNoUnfinishedForResourceGroupAsync(
        string tenantId,
        string resourceGroupId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result> EnsureNoUnfinishedForIsolationScopeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public interface IResourceDeploymentComputeProfileReferenceGuard
{
    Task<Result> EnsureUnreferencedAsync(
        string computeProfileId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public interface IResourceDeploymentQueryService
{
    Task<PlacementSummaryDto?> GetAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, PlacementSummaryDto>> GetManyAsync(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default);
}

public interface IResourceDeploymentComputeLookup
{
    Task<Result<CurrentResourceDeploymentComputeDto>> GetCurrentAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Multi-target read capability. Existing single-target callers intentionally
/// continue to use <see cref="IResourceDeploymentQueryService"/>.
/// </summary>
public interface IResourceDeploymentSetQueryService
{
    Task<IReadOnlyList<PlacementSummaryDto>> GetAllAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default);

    Task<PlacementSummaryDto?> GetExactAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string deploymentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<PlacementSummaryDto>>> GetAllManyAsync(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default);
}
