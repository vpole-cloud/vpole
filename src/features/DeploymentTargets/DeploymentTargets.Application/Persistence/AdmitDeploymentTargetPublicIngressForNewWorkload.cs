using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

internal sealed class MongoAdmitDeploymentTargetPublicIngressForNewWorkload(
    AdmitDeploymentTargetForWorkload admitDeploymentTargetForWorkload,
    LoadEligibleDeploymentTargets loadEligibleDeploymentTargets)
{
    public async Task<DeploymentTargetPublicIngressAdmissionResult> InvokeAsync(
        DeploymentTargetAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.ExplicitDeploymentTargetId))
        {
            var resolved = await admitDeploymentTargetForWorkload(
                request.ExplicitDeploymentTargetId,
                cancellationToken);
            if (!resolved.TryGetValue(out var target, out var admissionError))
            {
                return admissionError switch
                {
                    DeploymentTargetNotFound error => error,
                    DeploymentTargetDisabled error => error,
                    DeploymentTargetDeletionRequested error => error,
                    DeploymentTargetNewWorkloadsDisabled error => error
                };
            }

            return DeploymentTargetAdmissionSupport.HasRequiredCapabilities(
                target,
                DeploymentTargetRequiredCapabilities.PublicIngress)
                ? DeploymentTargetAdmissionSupport.ToPublicIngressDto(target)
                : new DeploymentTargetPublicIngressUnavailable(target.Id);
        }

        var eligible = await loadEligibleDeploymentTargets(
            DeploymentTargetRequiredCapabilities.PublicIngress,
            cancellationToken);
        return eligible.Count switch
        {
            0 => new NoDeploymentTargetAcceptsPublicWorkloads(),
            > 1 => new DeploymentTargetSelectionRequired(
                eligible.Select(target => target.Id).ToArray()),
            _ => DeploymentTargetAdmissionSupport.ToPublicIngressDto(eligible[0].ToLookupDto())
        };
    }
}
