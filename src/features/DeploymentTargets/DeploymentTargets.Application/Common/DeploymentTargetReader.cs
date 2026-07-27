using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Common;

public sealed class DeploymentTargetReader(
    IDeploymentTargetDbContext dbContext)
{
    public async Task<Result<DeploymentTarget>> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var target = await dbContext.DeploymentTargets
            .Find(item => item.Id == id)
            .FirstOrDefaultAsync(cancellationToken);

        return target is null
            ? Failures.NotFound("Deployment target not found.", id)
            : target;
    }

    public async Task<string?> GetDefaultIdAsync(
        CancellationToken cancellationToken = default) =>
        await dbContext.DeploymentTargetSettings
            .Find(settings => settings.Id == DeploymentTargetSettings.SingletonId)
            .Project(settings => settings.DefaultDeploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);
}
