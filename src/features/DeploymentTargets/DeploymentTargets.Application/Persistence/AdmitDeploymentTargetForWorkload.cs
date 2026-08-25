using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

internal sealed class MongoAdmitDeploymentTargetForWorkload(
    LoadDeploymentTargetById loadDeploymentTargetById)
{
    public async Task<Result<
        DeploymentTargetLookupDto,
        DeploymentTargetWorkloadAdmissionError>> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var normalizedDeploymentTargetId =
            DeploymentTargetAdmissionSupport.NormalizeDeploymentTargetId(deploymentTargetId);
        var target = await loadDeploymentTargetById(normalizedDeploymentTargetId, cancellationToken);
        return target is null
            ? new Err<DeploymentTargetWorkloadAdmissionError>(
                new DeploymentTargetWorkloadAdmissionError(
                    new DeploymentTargetNotFound(normalizedDeploymentTargetId)))
            : DeploymentTargetAdmissionSupport.AdmitForWorkload(target);
    }
}
