using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence.Admission;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Reads;

internal sealed class MongoLookupDeploymentTarget(LoadDeploymentTargetById loadDeploymentTargetById)
{
    public async Task<LookupDeploymentTargetOutcome> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var normalizedDeploymentTargetId =
            DeploymentTargetAdmissionSupport.NormalizeDeploymentTargetId(deploymentTargetId);
        var target = await loadDeploymentTargetById(normalizedDeploymentTargetId, cancellationToken);
        return target is null
            ? new DeploymentTargetLookupError(
                new DeploymentTargetNotFound(normalizedDeploymentTargetId))
            : target.ToLookupDto();
    }
}
