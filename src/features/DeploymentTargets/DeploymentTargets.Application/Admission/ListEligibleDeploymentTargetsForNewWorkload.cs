using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Contracts;

namespace Pole.Features.DeploymentTargets.Application.Admission;

internal sealed class MongoListEligibleDeploymentTargetsForNewWorkload(
    LoadEligibleDeploymentTargets loadEligibleDeploymentTargets)
{
    public async Task<IReadOnlyList<DeploymentTargetLookupDto>> InvokeAsync(
        CancellationToken cancellationToken = default) => (await loadEligibleDeploymentTargets(
            DeploymentTargetRequiredCapabilities.None,
            cancellationToken))
        .Select(target => target.ToLookupDto())
        .ToArray();
}
