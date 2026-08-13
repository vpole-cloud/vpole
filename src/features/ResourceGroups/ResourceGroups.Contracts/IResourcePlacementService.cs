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

    async Task<Result<IReadOnlyList<ResourcePlacement>>> ResolveOrCreateManyAsync(
        string tenantId,
        string resourceGroupId,
        string? isolationScopeId,
        IReadOnlyList<string> deploymentTargetIds,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deploymentTargetIds);
        var placements = new List<ResourcePlacement>(deploymentTargetIds.Count);
        foreach (var targetId in deploymentTargetIds)
        {
            var result = await ResolveOrCreateAsync(
                tenantId,
                resourceGroupId,
                isolationScopeId,
                targetId,
                session,
                cancellationToken);
            if (!result.TryGetValue(out var placement, out var failure))
                return failure;
            placements.Add(placement);
        }

        return placements;
    }
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
