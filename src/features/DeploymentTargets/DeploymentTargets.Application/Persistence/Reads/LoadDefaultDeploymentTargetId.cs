using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

public delegate Task<string?> LoadDefaultDeploymentTargetId(
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadDefaultDeploymentTargetId(
    IMongoCollection<DeploymentTargetSettings> settings)
{
    public async Task<string?> InvokeAsync(CancellationToken cancellationToken = default) =>
        await settings
            .Find(DeploymentTargetSettingsFilters.Singleton())
            .Project(item => item.DefaultDeploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);
}
