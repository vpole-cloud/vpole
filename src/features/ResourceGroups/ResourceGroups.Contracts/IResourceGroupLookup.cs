using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Contracts;

public sealed record ResourceGroupOwnershipNotFound(string ResourceGroupId);

public sealed record ResourceGroupOwnershipDeleting(string ResourceGroupId);

public union ResourceGroupOwnershipError(
    ResourceGroupOwnershipNotFound,
    ResourceGroupOwnershipDeleting);

public delegate Task<Result<ResourceGroupOwnership, ResourceGroupOwnershipError>>
    GetResourceGroupOwnership(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default);

public sealed record ResourceGroupOwnership(
    string ResourceGroupId,
    string TenantId,
    string DefaultIsolationScopeId,
    string KubernetesNamespace,
    ResourceGroupOwnershipLifecycle Lifecycle);

/// <summary>
/// Transitional namespace projection for consumers that do not yet adopt RuntimeCell placement.
/// </summary>
public sealed record ResourceGroupPlacement(
    string ResourceGroupId,
    string TenantId,
    string KubernetesNamespace);

public enum ResourceGroupOwnershipLifecycle
{
    Active,
    Deleting
}
