using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Common;

public sealed class DeploymentTargetReferenceAdmission(
    IDeploymentTargetDbContext dbContext) : IDeploymentTargetReferenceAdmission
{
    public Task<Result> FenceCreationAsync(
        string deploymentTargetId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        AdvanceFenceAsync(
            deploymentTargetId,
            session,
            requireAdmission: true,
            cancellationToken);

    public Task<Result> FenceRemovalAsync(
        string deploymentTargetId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        AdvanceFenceAsync(
            deploymentTargetId,
            session,
            requireAdmission: false,
            cancellationToken);

    private async Task<Result> AdvanceFenceAsync(
        string deploymentTargetId,
        IClientSessionHandle session,
        bool requireAdmission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Deployment-target reference fencing must run inside the reference owner's MongoDB transaction.");
        }

        if (!ObjectId.TryParse(deploymentTargetId, out _))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["DeploymentTargetId"] = ["Deployment target ID must be a MongoDB ObjectId."]
            });
        }

        var filter = Builders<DeploymentTarget>.Filter.And(
            Builders<DeploymentTarget>.Filter.Eq(target => target.Id, deploymentTargetId),
            Builders<DeploymentTarget>.Filter.Lt(target => target.ReferenceFence, long.MaxValue));
        if (requireAdmission)
        {
            filter &= Builders<DeploymentTarget>.Filter.And(
                Builders<DeploymentTarget>.Filter.Eq(target => target.Enabled, true),
                Builders<DeploymentTarget>.Filter.Eq(target => target.AcceptsWorkloads, true),
                Builders<DeploymentTarget>.Filter.Eq(
                    target => target.DeletionRequestedAtUtc,
                    null));
        }

        var update = await dbContext.DeploymentTargets.UpdateOneAsync(
            session,
            filter,
            Builders<DeploymentTarget>.Update.Inc(target => target.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        if (update.ModifiedCount == 1)
            return Result.Success();

        return requireAdmission
            ? Failures.Conflict("The deployment target no longer accepts durable references.")
            : Failures.Conflict("The deployment target is unavailable for reference removal.");
    }
}
