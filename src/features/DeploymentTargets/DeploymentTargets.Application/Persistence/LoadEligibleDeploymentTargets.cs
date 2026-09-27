using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

public delegate Task<IReadOnlyList<DeploymentTarget>> LoadEligibleDeploymentTargets(
    DeploymentTargetRequiredCapabilities requiredCapabilities,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadEligibleDeploymentTargets(
    IMongoCollection<DeploymentTarget> deploymentTargets)
{
    public async Task<IReadOnlyList<DeploymentTarget>> InvokeAsync(
        DeploymentTargetRequiredCapabilities requiredCapabilities,
        CancellationToken cancellationToken = default)
    {
        var targets = await deploymentTargets
            .Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .SortBy(target => target.Id)
            .ToListAsync(cancellationToken);
        return targets
            .Where(target => !requiredCapabilities.HasFlag(
                    DeploymentTargetRequiredCapabilities.PublicIngress) ||
                target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null &&
                target.WorkloadConfiguration.NetworkSources.PublicIngress is not null)
            .Where(target => !requiredCapabilities.HasFlag(
                    DeploymentTargetRequiredCapabilities.FunctionRuntimeConnectivity) ||
                target.WorkloadConfiguration.NetworkSources.FunctionGateway is not null)
            .ToArray();
    }
}
