using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence.Admission;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Reads;

internal sealed class MongoLookupDefaultDeploymentTarget(
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
    LoadDeploymentTargetById loadDeploymentTargetById)
{
    public async Task<Result<DeploymentTargetLookupDto, DefaultDeploymentTargetLookupError>> InvokeAsync(
        CancellationToken cancellationToken = default)
    {
        var target = await DeploymentTargetAdmissionSupport.LoadDefaultAsync(
            loadDefaultDeploymentTargetId,
            loadDeploymentTargetById,
            cancellationToken);
        return target switch
        {
            Ok<DeploymentTarget> value => new Ok<DeploymentTargetLookupDto>(value.Value.ToLookupDto()),
            Err<DefaultDeploymentTargetLookupError> error => error
        };
    }
}
