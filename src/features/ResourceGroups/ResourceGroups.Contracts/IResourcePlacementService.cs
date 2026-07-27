using MongoDB.Driver;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Contracts;

public interface IResourcePlacementService
{
    Task<Result<ResourcePlacement>> ResolveOrCreateAsync(
        string tenantId,
        string resourceGroupId,
        string? isolationScopeId,
        string? deploymentTargetId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public sealed record ResourcePlacement(
    string ResourceGroupId,
    string IsolationScopeId,
    string RuntimeCellId,
    string DeploymentTargetId,
    string KubernetesNamespace,
    ResourcePlacementState State);

public enum ResourcePlacementState
{
    Provisioning,
    Ready,
    Failed,
    Deleting,
    Deleted
}
