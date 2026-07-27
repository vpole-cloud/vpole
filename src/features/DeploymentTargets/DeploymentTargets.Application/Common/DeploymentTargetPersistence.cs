using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Common;

public sealed class DeploymentTargetPersistence(
    IDeploymentTargetDbContext dbContext)
{
    public async Task<Result<DeploymentTarget>> InsertAsync(
        DeploymentTarget target,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await dbContext.DeploymentTargets.InsertOneAsync(
                target,
                cancellationToken: cancellationToken);
            return target;
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return Failures.Conflict(
                "A deployment target for this Kubernetes cluster already exists.");
        }
    }
}
