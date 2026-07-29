using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Placement;

public interface IResourceDeploymentPlacementResolver
{
    Task<Result<ResolvedDeploymentTarget>> ResolveTargetAsync(
        string? deploymentTargetId,
        bool requiresIngress,
        CancellationToken cancellationToken = default);
}

public sealed record ResolvedDeploymentTarget(
    string DeploymentTargetId,
    long WorkloadConfigurationRevision,
    ResolvedIngressConfiguration? Ingress);

public sealed class ResourceDeploymentPlacementResolver(
    IDeploymentTargetLookup deploymentTargetLookup)
    : IResourceDeploymentPlacementResolver
{
    public async Task<Result<ResolvedDeploymentTarget>> ResolveTargetAsync(
        string? deploymentTargetId,
        bool requiresIngress,
        CancellationToken cancellationToken = default)
    {
        DeploymentTargetLookupDto target;
        if (!string.IsNullOrWhiteSpace(deploymentTargetId))
        {
            var explicitTarget = await deploymentTargetLookup
                .ResolveForNewWorkloadAsync(
                    deploymentTargetId.Trim(),
                    cancellationToken);
            if (!explicitTarget.TryGetValue(out var resolvedTarget, out var failure))
                return failure;
            target = resolvedTarget;
        }
        else
        {
            var eligibleResult = await deploymentTargetLookup
                .ListEligibleForNewWorkloadAsync(cancellationToken);
            if (!eligibleResult.TryGetValue(out var eligible, out var failure))
                return failure;

            if (eligible.Count == 0)
                return Failures.Conflict("No deployment target accepts new workloads.");
            if (eligible.Count > 1)
            {
                return Failures.ValidationFailure(new Dictionary<string, string[]>
                {
                    ["Placement.DeploymentTargetId"] =
                    [
                        "Deployment target is required when more than one target accepts workloads."
                    ]
                });
            }

            target = eligible[0];
        }

        var ingress = target.WorkloadConfiguration.Ingress;
        if (requiresIngress && ingress is null)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Placement.DeploymentTargetId"] =
                [
                    "The selected deployment target has no ingress configuration."
                ]
            });
        }

        return new ResolvedDeploymentTarget(
            target.Id,
            target.WorkloadConfiguration.Revision,
            ingress is null
                ? null
                : ResolvedIngressConfiguration.Create(
                    ingress.IngressClassName,
                    ingress.ClusterIssuerName));
    }
}
