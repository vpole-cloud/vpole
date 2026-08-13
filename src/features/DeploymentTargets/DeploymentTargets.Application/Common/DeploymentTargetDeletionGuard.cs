using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Common;

public interface IDeploymentTargetDeletionGuard
{
    Task<Result> EnsureNoReferencesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default);
}

public sealed class DeploymentTargetDeletionGuard(
    IDeploymentTargetDbContext dbContext) : IDeploymentTargetDeletionGuard
{
    public async Task<Result> EnsureNoReferencesAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var reference = await dbContext.DeploymentTargetReferences
            .Find(DeploymentTargetReferenceFilters.ForTarget(deploymentTargetId))
            .Project(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return reference is null
            ? Result.Success()
            : Failures.Conflict("The deployment target still has durable references.");
    }
}
