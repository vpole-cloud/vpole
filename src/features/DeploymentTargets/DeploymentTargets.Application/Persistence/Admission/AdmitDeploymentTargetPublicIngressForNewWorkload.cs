using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Admission;

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
            if (resolved is DeploymentTargetWorkloadAdmissionError admissionError)
            {
                return admissionError switch
                {
                    DeploymentTargetNotFound error => error,
                    DeploymentTargetDisabled error => error,
                    DeploymentTargetDeletionRequested error => error,
                    DeploymentTargetNewWorkloadsDisabled error => error
                };
            }
            var target = (DeploymentTargetLookupDto)resolved.Value;

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
