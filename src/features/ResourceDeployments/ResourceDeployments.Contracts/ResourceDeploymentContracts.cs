using System.Collections.Immutable;
using MongoDB.Driver;
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

[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record PlacementSummaryDto(
    string DeploymentId,
    string IsolationScopeId,
    string RuntimeCellId,
    string DeploymentTargetId,
    long DesiredGeneration,
    long ObservedGeneration,
    ResourceDeploymentStatus Status,
    string? FailureCode,
    ComputeConfigurationSummaryDto Compute,
    ImmutableList<ResourceDeploymentEndpointDto> Endpoints,
    long ResourceRevision,
    int FixedReplicas,
    ResourceDeploymentIngressConfiguration? Ingress,
    long DeploymentRevision,
    ResourceDeploymentDesiredOperation DesiredOperation,
    ResourceDeploymentIsolationDto Isolation);

public sealed record ResourceDeploymentIsolationDto(
    string Enforcement,
    string Readiness,
    string Reason,
    DateTime? LastCheckedAtUtc);

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
    long DesiredGeneration,
    ResourceDeploymentComputeConfiguration Compute,
    ResourceDeploymentStatus Status,
    ResourceDeploymentDesiredOperation DesiredOperation,
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

public union LoadCurrentResourceDeploymentComputeOutcome(
    CurrentResourceDeploymentComputeDto,
    LoadCurrentResourceDeploymentComputeError);

public sealed record ResourceGroupDeploymentsSettled;
public union EnsureResourceGroupDeploymentsSettledOutcome(
    ResourceGroupDeploymentsSettled,
    ResourceGroupHasUnfinishedDeployments);

public sealed record IsolationScopeDeploymentsSettled;
public union EnsureIsolationScopeDeploymentsSettledOutcome(
    IsolationScopeDeploymentsSettled,
    IsolationScopeHasUnfinishedDeployments);

public sealed record ComputeProfileUnreferenced;
public union EnsureComputeProfileUnreferencedOutcome(
    ComputeProfileUnreferenced,
    ComputeProfileReferencedByResourceDeployment);

public delegate Task<EnsureResourceGroupDeploymentsSettledOutcome>
    EnsureNoUnfinishedResourceGroupDeployments(
    string tenantId,
    string resourceGroupId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<EnsureIsolationScopeDeploymentsSettledOutcome>
    EnsureNoUnfinishedIsolationScopeDeployments(
    string tenantId,
    string resourceGroupId,
    string isolationScopeId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<EnsureComputeProfileUnreferencedOutcome>
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

public delegate Task<LoadCurrentResourceDeploymentComputeOutcome>
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

// Internal server-resolved identity for policy construction. This is never a
// tenant-facing placement summary or caller-provided selector.
public sealed record ResourceDeploymentConnectionPlacement(
    string DeploymentId,
    string DeploymentTargetId,
    string RuntimeCellId,
    string KubernetesNamespace,
    bool DeletionRequested);

public delegate Task<IReadOnlyList<ResourceDeploymentConnectionPlacement>>
    LoadResourceDeploymentConnectionPlacements(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    IClientSessionHandle session,
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

public delegate Task<IReadOnlyList<ResourceDeploymentRuntimePlacementDto>>
    LoadResourceDeploymentRuntimePlacements(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds,
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
