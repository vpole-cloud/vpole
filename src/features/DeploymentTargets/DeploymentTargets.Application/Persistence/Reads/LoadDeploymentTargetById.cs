using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

public delegate Task<DeploymentTarget?> LoadDeploymentTargetById(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadDeploymentTargetById(
    IMongoCollection<DeploymentTarget> deploymentTargets)
{
    public async Task<DeploymentTarget?> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default) =>
        await deploymentTargets
            .Find(DeploymentTargetFilters.ById(deploymentTargetId))
            .FirstOrDefaultAsync(cancellationToken);
}
