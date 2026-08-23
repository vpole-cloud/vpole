using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Placement;

internal sealed class ResourceDeploymentPlacementResolver(
    AdmitDeploymentTargetForWorkload admitDeploymentTarget,
    ListEligibleDeploymentTargetsForNewWorkload listEligibleDeploymentTargets)
{
    public async Task<Result<ResolvedResourceDeploymentTarget>> InvokeAsync(
        string? deploymentTargetId,
        bool requiresIngress,
        CancellationToken cancellationToken = default)
    {
        DeploymentTargetLookupDto target;
        if (!string.IsNullOrWhiteSpace(deploymentTargetId))
        {
            var explicitTarget = await admitDeploymentTarget(
                deploymentTargetId.Trim(),
                cancellationToken);
            if (explicitTarget is Err<DeploymentTargetWorkloadAdmissionError> error)
                return AdmissionFailure(error.Value);
            if (explicitTarget is not Ok<DeploymentTargetLookupDto> resolvedTarget)
            {
                throw new InvalidOperationException(
                    "Deployment-target admission returned an unsupported result case.");
            }

            target = resolvedTarget.Value;
        }
        else
        {
            var eligible = await listEligibleDeploymentTargets(cancellationToken);

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

    private static Failure AdmissionFailure(DeploymentTargetWorkloadAdmissionError error) => error switch
    {
        InvalidDeploymentTargetId => Failures.ValidationFailure(
            new Dictionary<string, string[]>
            {
                ["DeploymentTargetId"] = ["Deployment target ID must be a MongoDB ObjectId."]
            }),
        DeploymentTargetNotFound notFound => Failures.NotFound(
            "Deployment target not found.",
            notFound.DeploymentTargetId),
        DeploymentTargetDisabled => Failures.Conflict("The deployment target is disabled."),
        DeploymentTargetDeletionRequested => Failures.Conflict(
            "The deployment target is pending deletion."),
        DeploymentTargetNewWorkloadsDisabled => Failures.Conflict(
            "The deployment target does not accept new workloads."),
        _ => throw new InvalidOperationException(
            "Deployment-target admission returned an unsupported error case.")
    };
}
