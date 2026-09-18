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
    string DesiredOperation = "Apply")
{
    public ResourceDeploymentIsolationDto Isolation { get; init; } =
        new("Unknown", "EnforcementRequired", "EnforcementNotEstablished", null);
}

public sealed record ResourceDeploymentIsolationDto(
    string Enforcement, string Readiness, string Reason, DateTime? LastCheckedAtUtc);

public sealed record ResourceDeploymentDto(
    string Id,
    string ResourceType,
    string ResourceId,
    string ResourceGroupId,
    PlacementSummaryDto Placement,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// Internal workload-runtime projection for resolving a persisted physical placement.
/// Kubernetes namespaces are deliberately kept out of tenant-facing placement summaries.
/// </summary>
public sealed record ResourceDeploymentRuntimePlacementDto(
    string DeploymentId,
    string ResourceId,
    string RuntimeCellId,
    string DeploymentTargetId,
    string KubernetesNamespace,
    long DeploymentRevision,
    ResourceDeploymentComputeConfiguration Compute,
    string Status,
    string DesiredOperation,
    string IsolationScopeId);

public sealed record ResourceGroupHasUnfinishedDeployments(string ResourceGroupId);

public sealed record IsolationScopeHasUnfinishedDeployments(
    string ResourceGroupId,
    string IsolationScopeId);

public sealed record ComputeProfileReferencedByResourceDeployment(
    string ComputeProfileId);

public sealed record CurrentResourceDeploymentComputeNotFound(string ResourceId);

public sealed record CurrentResourceDeploymentComputeCardinalityConflict(
    string ResourceId);

public union LoadCurrentResourceDeploymentComputeError(
    CurrentResourceDeploymentComputeNotFound,
    CurrentResourceDeploymentComputeCardinalityConflict);

public delegate Task<Result<Unit, ResourceGroupHasUnfinishedDeployments>>
    EnsureNoUnfinishedResourceGroupDeployments(
    string tenantId,
    string resourceGroupId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<Result<Unit, IsolationScopeHasUnfinishedDeployments>>
    EnsureNoUnfinishedIsolationScopeDeployments(
    string tenantId,
    string resourceGroupId,
    string isolationScopeId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<Result<Unit, ComputeProfileReferencedByResourceDeployment>>
    EnsureComputeProfileUnreferenced(
    string computeProfileId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<PlacementSummaryDto?> LoadResourceDeployment(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyDictionary<string, PlacementSummaryDto>>
    LoadResourceDeployments(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default);

public delegate Task<Result<
    CurrentResourceDeploymentComputeDto,
    LoadCurrentResourceDeploymentComputeError>>
    LoadCurrentResourceDeploymentCompute(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<PlacementSummaryDto>> LoadResourceDeploymentSet(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    CancellationToken cancellationToken = default);

public delegate Task<PlacementSummaryDto?> LoadExactResourceDeployment(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    string deploymentId,
    CancellationToken cancellationToken = default);

public delegate Task<ResourceDeploymentRuntimePlacementDto?>
    LoadExactResourceDeploymentRuntimePlacement(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string deploymentId,
        CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<string>> LoadResourceDeploymentTargetIds(
    ResourceTypeId resourceType,
    CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyDictionary<string, IReadOnlyList<PlacementSummaryDto>>>
    LoadResourceDeploymentSets(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default);
