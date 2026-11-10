using MongoDB.Driver;

namespace Pole.Features.ResourceGroups.Contracts;

public sealed record ResourceGroupPlacementResourceGroupNotFound(
    string ResourceGroupId
);

public sealed record ResourceGroupPlacementResourceGroupDeleting(
    string ResourceGroupId
);

public sealed record ResourceGroupPlacementIsolationScopeNotFound(
    string IsolationScopeId
);

public sealed record ResourceGroupPlacementIsolationScopeDeleting(
    string IsolationScopeId
);

public sealed record ResourceGroupPlacementDeploymentTargetRejected(
    string? DeploymentTargetId
);

public sealed record ResourceGroupPlacementDeploymentTargetReferenceRejected(
    string DeploymentTargetId
);

public sealed record ResourceGroupPlacementTargetResolutionChanged(
    string RequestedDeploymentTargetId,
    string ResolvedDeploymentTargetId
);

public sealed record ResourceGroupPlacementChanged(
    string ResourceGroupId
);

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
    ResourcePlacementState State
);

public enum ResourcePlacementState
{
    Provisioning,
    Ready,
    Failed,
    Deleting,
    Deleted,
}

public union ResolveResourceGroupPlacementOutcome(ResourcePlacement, ResourceGroupPlacementError);

public sealed record ResourcePlacements(
    IReadOnlyList<ResourcePlacement> Items
);

public union ResolveResourceGroupPlacementsOutcome(ResourcePlacements, ResourceGroupPlacementError);

public delegate Task<ResolveResourceGroupPlacementOutcome> ResolveResourceGroupPlacement(
    ResolveResourceGroupPlacementRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<ResolveResourceGroupPlacementsOutcome> ResolveResourceGroupPlacements(
    ResolveResourceGroupPlacementsRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<ResourcePlacement?> LoadResourcePlacement(
    string tenantId,
    string runtimeCellId,
    CancellationToken cancellationToken = default);

public delegate Task<ResourcePlacement?> LoadResourcePlacementInTransaction(
    LoadResourcePlacementInTransactionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<IReadOnlyList<ResourcePlacement>> LoadResourcePlacementsInTransaction(
    LoadResourcePlacementsInTransactionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public sealed record ResolveResourceGroupPlacementRequest(
    string TenantId,
    string ResourceGroupId,
    string? IsolationScopeId,
    string? DeploymentTargetId
);

public sealed record ResolveResourceGroupPlacementsRequest(
    string TenantId,
    string ResourceGroupId,
    string? IsolationScopeId,
    IReadOnlyList<string> DeploymentTargetIds
);

public sealed record LoadResourcePlacementInTransactionRequest(
    string TenantId,
    string RuntimeCellId
);

public sealed record LoadResourcePlacementsInTransactionRequest(
    string TenantId,
    IReadOnlyCollection<string> RuntimeCellIds
);
