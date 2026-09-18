using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Common;

/// <summary>Called only after tenant-scoped deployment reads. Never projects provider diagnostics.</summary>
public sealed class ResourceDeploymentIsolationProjection(LookupDeploymentTarget lookupTarget)
{
    public async Task<PlacementSummaryDto> ProjectAsync(PlacementSummaryDto summary, CancellationToken cancellationToken)
    {
        var result = await lookupTarget(summary.DeploymentTargetId, cancellationToken);
        var policy = result.TryGetValue(out var target, out _)
            ? target.NetworkPolicy : DeploymentTargetNetworkPolicyDto.Unknown;
        return summary with
        {
            Isolation = new(policy.Enforcement, policy.Readiness, policy.Reason, policy.LastCheckedAtUtc)
        };
    }

    public async Task<IReadOnlyDictionary<string, ResourceDeploymentIsolationDto>> LoadAsync(
        IEnumerable<string> targetIds, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, ResourceDeploymentIsolationDto>(StringComparer.Ordinal);
        foreach (var targetId in targetIds.Distinct(StringComparer.Ordinal))
        {
            var result = await lookupTarget(targetId, cancellationToken);
            var policy = result.TryGetValue(out var target, out _)
                ? target.NetworkPolicy : DeploymentTargetNetworkPolicyDto.Unknown;
            values.Add(targetId, new(policy.Enforcement, policy.Readiness, policy.Reason, policy.LastCheckedAtUtc));
        }
        return values;
    }
}
