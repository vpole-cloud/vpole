using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

internal sealed class MongoAdmitDefaultDeploymentTargetForNewWorkload(
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
    LoadDeploymentTargetById loadDeploymentTargetById)
{
    public async Task<Result<
        DeploymentTargetLookupDto,
        DefaultDeploymentTargetWorkloadAdmissionError>> InvokeAsync(
        CancellationToken cancellationToken = default)
    {
        var target = await DeploymentTargetAdmissionSupport.LoadDefaultAsync(
            loadDefaultDeploymentTargetId,
            loadDeploymentTargetById,
            cancellationToken);
        return target switch
        {
            Ok<DeploymentTarget> value =>
                DeploymentTargetAdmissionSupport.AdmitDefaultForNewWorkload(value.Value),
            Err<DefaultDeploymentTargetLookupError> error => error.Value switch
            {
                DefaultDeploymentTargetNotConfigured notConfigured =>
                    new Err<DefaultDeploymentTargetWorkloadAdmissionError>(
                        new DefaultDeploymentTargetWorkloadAdmissionError(notConfigured))
            }
        };
    }
}
