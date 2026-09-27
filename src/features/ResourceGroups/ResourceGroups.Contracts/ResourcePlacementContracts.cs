using MongoDB.Driver;

namespace Pole.Features.ResourceGroups.Contracts;

public sealed record ResourceGroupPlacementResourceGroupNotFound(string ResourceGroupId);

public sealed record ResourceGroupPlacementResourceGroupDeleting(string ResourceGroupId);

public sealed record ResourceGroupPlacementIsolationScopeNotFound(string IsolationScopeId);

public sealed record ResourceGroupPlacementIsolationScopeDeleting(string IsolationScopeId);

public sealed record ResourceGroupPlacementDeploymentTargetRejected(
    string? DeploymentTargetId);

public sealed record ResourceGroupPlacementDeploymentTargetReferenceRejected(
    string DeploymentTargetId);

public sealed record ResourceGroupPlacementTargetResolutionChanged(
    string RequestedDeploymentTargetId,
    string ResolvedDeploymentTargetId);

public sealed record ResourceGroupPlacementChanged(string ResourceGroupId);

public union ResourceGroupPlacementError(
    ResourceGroupPlacementResourceGroupNotFound,
    ResourceGroupPlacementResourceGroupDeleting,
    ResourceGroupPlacementIsolationScopeNotFound,
    ResourceGroupPlacementIsolationScopeDeleting,
    ResourceGroupPlacementDeploymentTargetRejected,
    ResourceGroupPlacementDeploymentTargetReferenceRejected,
    ResourceGroupPlacementTargetResolutionChanged,
    ResourceGroupPlacementChanged);

public static class ResourcePlacementReferenceOwnerTypes
{
    public const string RuntimeCell = "RuntimeCell";
}

public sealed record ResourcePlacement(
    string ResourceGroupId,
    string IsolationScopeId,
    string RuntimeCellId,
    string DeploymentTargetId,
    string KubernetesNamespace,
    long RuntimeCellGeneration,
    ResourcePlacementState State);

public enum ResourcePlacementState
{
    Provisioning,
    Ready,
    Failed,
    Deleting,
    Deleted
}

public union ResolveResourceGroupPlacementOutcome(
    ResourcePlacement,
    ResourceGroupPlacementError);

public sealed record ResourcePlacements(IReadOnlyList<ResourcePlacement> Items);

public union ResolveResourceGroupPlacementsOutcome(
    ResourcePlacements,
    ResourceGroupPlacementError);

public delegate Task<ResolveResourceGroupPlacementOutcome>
    ResolveResourceGroupPlacement(
        string tenantId,
        string resourceGroupId,
        string? isolationScopeId,
        string? deploymentTargetId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<ResolveResourceGroupPlacementsOutcome>
    ResolveResourceGroupPlacements(
        string tenantId,
        string resourceGroupId,
        string? isolationScopeId,
        IReadOnlyList<string> deploymentTargetIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<ResourcePlacement?> LoadResourcePlacement(
    string tenantId,
    string runtimeCellId,
    CancellationToken cancellationToken = default);

public delegate Task<ResourcePlacement?> LoadResourcePlacementInTransaction(
    string tenantId,
    string runtimeCellId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<ResourcePlacement>> LoadResourcePlacementsInTransaction(
    string tenantId,
    IReadOnlyCollection<string> runtimeCellIds,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
