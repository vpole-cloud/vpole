using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Admission;

internal sealed class MongoAdmitDeploymentTargetForNewWorkload(
    AdmitDeploymentTargetForWorkload admitDeploymentTargetForWorkload,
    LoadEligibleDeploymentTargets loadEligibleDeploymentTargets)
{
    public async Task<AdmitDeploymentTargetForNewWorkloadOutcome> InvokeAsync(
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
                return new DeploymentTargetNewWorkloadAdmissionError(admissionError);
            }
            var target = (DeploymentTargetLookupDto)resolved.Value;

            return DeploymentTargetAdmissionSupport.HasRequiredCapabilities(
                target,
                request.RequiredCapabilities)
                ? target
                : new DeploymentTargetNewWorkloadAdmissionError(
                        new DeploymentTargetRequiredCapabilitiesUnavailable(
                            target.Id,
                            request.RequiredCapabilities));
        }

        var eligible = (await loadEligibleDeploymentTargets(
                request.RequiredCapabilities,
                cancellationToken))
            .Select(target => target.ToLookupDto())
            .ToArray();
        return eligible.Length switch
        {
            0 => new DeploymentTargetNewWorkloadAdmissionError(
                    new NoDeploymentTargetAcceptsWorkloads()),
            > 1 => new DeploymentTargetNewWorkloadAdmissionError(
                    new DeploymentTargetWorkloadSelectionRequired(
                        eligible.Select(target => target.Id).ToArray())),
            _ => eligible[0]
        };
    }
}
