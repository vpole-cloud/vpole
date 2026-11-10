using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Admission;

internal sealed class MongoAdmitDefaultDeploymentTargetForNewWorkload(
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
    LoadDeploymentTargetById loadDeploymentTargetById
)
{
    public async Task<AdmitDefaultDeploymentTargetForNewWorkloadOutcome> InvokeAsync(
        CancellationToken cancellationToken = default)
    {
        var target = await DeploymentTargetAdmissionSupport.LoadDefaultAsync(
            loadDefaultDeploymentTargetId,
            loadDeploymentTargetById,
            cancellationToken);
        return target switch
        {
            DeploymentTarget value => DeploymentTargetAdmissionSupport.AdmitDefaultForNewWorkload(value),
            DefaultDeploymentTargetLookupError error => error switch
            {
                DefaultDeploymentTargetNotConfigured notConfigured => new DefaultDeploymentTargetWorkloadAdmissionError(
                    notConfigured),
            },
        };
    }
}
