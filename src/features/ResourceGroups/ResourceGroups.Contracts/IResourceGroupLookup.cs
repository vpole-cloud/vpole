using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Contracts;

/// <summary>
/// Narrow ownership boundary for logical resources. It deliberately excludes the
/// transitional Kubernetes placement lookup retained for older adopters.
/// </summary>
public interface IResourceGroupOwnershipLookup
{
    Task<Result<ResourceGroupOwnership>> GetOwnershipAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default);
}

public interface IResourceGroupLookup
{
    Task<Result<ResourceGroupOwnership>> GetOwnershipAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transitional placement contract for resource features that have not adopted RuntimeCell yet.
    /// New resource placement should use <see cref="IResourcePlacementService"/>.
    /// </summary>
    Task<Result<ResourceGroupPlacement>> GetPlacementAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default);
}

public sealed record ResourceGroupOwnership(
    string ResourceGroupId,
    string TenantId,
    string DefaultIsolationScopeId,
    ResourceGroupOwnershipLifecycle Lifecycle);

public enum ResourceGroupOwnershipLifecycle
{
    Active,
    Deleting
}

public sealed record ResourceGroupPlacement(
    string ResourceGroupId,
    string TenantId,
    string KubernetesNamespace);
