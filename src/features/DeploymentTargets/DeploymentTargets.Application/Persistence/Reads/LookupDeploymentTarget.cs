using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

internal sealed class MongoLookupDeploymentTarget(LoadDeploymentTargetById loadDeploymentTargetById)
{
    public async Task<Result<DeploymentTargetLookupDto, DeploymentTargetLookupError>> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var normalizedDeploymentTargetId =
            DeploymentTargetAdmissionSupport.NormalizeDeploymentTargetId(deploymentTargetId);
        var target = await loadDeploymentTargetById(normalizedDeploymentTargetId, cancellationToken);
        return target is null
            ? new Err<DeploymentTargetLookupError>(
                new DeploymentTargetLookupError(
                    new DeploymentTargetNotFound(normalizedDeploymentTargetId)))
            : new Ok<DeploymentTargetLookupDto>(target.ToLookupDto());
    }
}
