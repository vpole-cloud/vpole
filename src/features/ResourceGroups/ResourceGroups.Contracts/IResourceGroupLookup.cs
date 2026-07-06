using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Contracts;

public interface IResourceGroupLookup
{
    Task<Result<ResourceGroupPlacement>> GetPlacementAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default);
}

public sealed record ResourceGroupPlacement(
    string ResourceGroupId,
    string TenantId,
    string KubernetesNamespace);
