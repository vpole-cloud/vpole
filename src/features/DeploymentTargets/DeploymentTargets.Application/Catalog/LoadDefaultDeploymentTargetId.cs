using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Catalog;

public delegate Task<string?> LoadDefaultDeploymentTargetId(
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadDefaultDeploymentTargetId(
    IMongoCollection<DeploymentTargetSettings> settings)
{
    public async Task<string?> InvokeAsync(CancellationToken cancellationToken = default) => await settings
            .Find(DeploymentTargetSettingsFilters.Singleton())
            .Project(item => item.DefaultDeploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);
}
