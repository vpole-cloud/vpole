using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Common;

/// <summary>Called only after tenant-scoped deployment reads. Never projects provider diagnostics.</summary>
public sealed class ResourceDeploymentIsolationProjection(
    LookupDeploymentTarget lookupTarget
)
{
    public async Task<PlacementSummaryDto> ProjectAsync(ResourceDeployment deployment, CancellationToken ct) =>
        deployment.ToPlacementSummaryDto(await LoadAsync(deployment.ResolvedPlacement.DeploymentTargetId, ct));

    public async Task<ResourceDeploymentDto> ProjectDtoAsync(ResourceDeployment deployment, CancellationToken ct) =>
        deployment.ToDto(await LoadAsync(deployment.ResolvedPlacement.DeploymentTargetId, ct));

    public async Task<IReadOnlyList<PlacementSummaryDto>> ProjectAsync(
        IReadOnlyCollection<ResourceDeployment> deployments,
        CancellationToken ct)
    {
        var policies = await LoadAsync(deployments.Select(item => item.ResolvedPlacement.DeploymentTargetId), ct);
        return deployments.Select(item =>
                item.ToPlacementSummaryDto(policies[item.ResolvedPlacement.DeploymentTargetId]))
            .ToArray();
    }

    public async Task<ResourceDeploymentIsolationDto> LoadAsync(string targetId, CancellationToken ct)
    {
        var result = await lookupTarget(targetId, ct);
        return FromPolicy(
            result is DeploymentTargetLookupDto target
                ? target.NetworkPolicy
                : DeploymentTargetNetworkPolicyDto.Unknown);
    }

    public async Task<IReadOnlyDictionary<string, ResourceDeploymentIsolationDto>> LoadAsync(
        IEnumerable<string> targetIds,
        CancellationToken ct)
    {
        var values = new Dictionary<string, ResourceDeploymentIsolationDto>(StringComparer.Ordinal);
        foreach (var targetId in targetIds.Distinct(StringComparer.Ordinal))
            values.Add(targetId, await LoadAsync(targetId, ct));
        return values;
    }

    internal static ResourceDeploymentIsolationDto FromPolicy(DeploymentTargetNetworkPolicyDto policy) => new(
        policy.Enforcement.ToString(),
        policy.Readiness.ToString(),
        policy.Reason.ToString(),
        policy.LastCheckedAtUtc);
}
