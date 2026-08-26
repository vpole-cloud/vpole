using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

public delegate Task<IReadOnlyList<DeploymentTarget>> LoadDeploymentTargets(
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadDeploymentTargets(
    IMongoCollection<DeploymentTarget> deploymentTargets)
{
    public async Task<IReadOnlyList<DeploymentTarget>> InvokeAsync(
        CancellationToken cancellationToken = default) =>
        await deploymentTargets
            .Find(DeploymentTargetFilters.All())
            .SortBy(target => target.DisplayName)
            .ThenBy(target => target.Id)
            .ToListAsync(cancellationToken);
}
