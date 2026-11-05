using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Features.DeploymentTargets.Application.Admission;

internal sealed class MongoListEligiblePublicIngressDeploymentTargetsForNewWorkload(
    LoadEligibleDeploymentTargets loadEligibleDeploymentTargets)
{
    public async Task<IReadOnlyList<DeploymentTargetPublicIngressDto>> InvokeAsync(
        DeploymentTargetAdmissionRequest request,
        CancellationToken cancellationToken = default) => (await loadEligibleDeploymentTargets(
            DeploymentTargetRequiredCapabilities.PublicIngress,
            cancellationToken))
        .Select(target => DeploymentTargetAdmissionSupport.ToPublicIngressDto(target.ToLookupDto()))
        .ToArray();
}
