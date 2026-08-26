using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Bootstrap;

internal sealed record DeploymentTargetBootstrapState(
    DeploymentTargetSettings? Settings,
    DeploymentTarget? FirstTarget,
    bool DefaultTargetExists);

internal delegate Task<DeploymentTargetBootstrapState> LoadDeploymentTargetBootstrapState(
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadDeploymentTargetBootstrapState(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    IMongoCollection<DeploymentTargetSettings> settings)
{
    public async Task<DeploymentTargetBootstrapState> InvokeAsync(
        CancellationToken cancellationToken = default)
    {
        var currentSettings = await settings
            .Find(DeploymentTargetSettingsFilters.Singleton())
            .FirstOrDefaultAsync(cancellationToken);
        var firstTarget = await deploymentTargets
            .Find(DeploymentTargetFilters.NotPendingDeletion())
            .SortBy(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var defaultTargetExists = currentSettings is not null &&
            await deploymentTargets.CountDocumentsAsync(
                DeploymentTargetFilters.ById(currentSettings.DefaultDeploymentTargetId),
                cancellationToken: cancellationToken) == 1;
        return new DeploymentTargetBootstrapState(
            currentSettings,
            firstTarget,
            defaultTargetExists);
    }
}
