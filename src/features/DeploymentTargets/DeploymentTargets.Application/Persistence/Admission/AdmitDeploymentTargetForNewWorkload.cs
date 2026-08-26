using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Admission;

internal sealed class MongoAdmitDeploymentTargetForNewWorkload(
    AdmitDeploymentTargetForWorkload admitDeploymentTargetForWorkload,
    LoadEligibleDeploymentTargets loadEligibleDeploymentTargets)
{
    public async Task<Result<
        DeploymentTargetLookupDto,
        DeploymentTargetNewWorkloadAdmissionError>> InvokeAsync(
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
                return new Err<DeploymentTargetNewWorkloadAdmissionError>(
                    new DeploymentTargetNewWorkloadAdmissionError(admissionError));
            }

            return DeploymentTargetAdmissionSupport.HasRequiredCapabilities(
                target,
                request.RequiredCapabilities)
                ? new Ok<DeploymentTargetLookupDto>(target)
                : new Err<DeploymentTargetNewWorkloadAdmissionError>(
                    new DeploymentTargetNewWorkloadAdmissionError(
                        new DeploymentTargetRequiredCapabilitiesUnavailable(
                            target.Id,
                            request.RequiredCapabilities)));
        }

        var eligible = (await loadEligibleDeploymentTargets(
                request.RequiredCapabilities,
                cancellationToken))
            .Select(target => target.ToLookupDto())
            .ToArray();
        return eligible.Length switch
        {
            0 => new Err<DeploymentTargetNewWorkloadAdmissionError>(
                new DeploymentTargetNewWorkloadAdmissionError(
                    new NoDeploymentTargetAcceptsWorkloads())),
            > 1 => new Err<DeploymentTargetNewWorkloadAdmissionError>(
                new DeploymentTargetNewWorkloadAdmissionError(
                    new DeploymentTargetWorkloadSelectionRequired(
                        eligible.Select(target => target.Id).ToArray()))),
            _ => new Ok<DeploymentTargetLookupDto>(eligible[0])
        };
    }
}
