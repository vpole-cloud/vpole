using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Placement;

internal sealed class ResourceDeploymentPlacementResolver(
    IDeploymentTargetLookup deploymentTargetLookup)
{
    public async Task<Result<ResolvedResourceDeploymentTarget>> InvokeAsync(
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
                return Failures.SemanticValidation(new Dictionary<string, string[]>
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
        if (requiresIngress &&
            (ingress is null || string.IsNullOrWhiteSpace(ingress.PublicBaseDomain)))
        {
            return Failures.SemanticValidation(new Dictionary<string, string[]>
            {
                ["Placement.DeploymentTargetId"] =
                [
                    "The selected deployment target has no public ingress base domain configuration."
                ]
            });
        }

        return new ResolvedResourceDeploymentTarget(
            target.Id,
            target.WorkloadConfiguration.Revision,
            ingress is null
                ? null
                : ResourceDeploymentIngressConfiguration.Create(
                    ingress.IngressClassName,
                    ingress.ClusterIssuerName,
                    ingress.PublicBaseDomain,
                    hostnames: null));
    }
}
