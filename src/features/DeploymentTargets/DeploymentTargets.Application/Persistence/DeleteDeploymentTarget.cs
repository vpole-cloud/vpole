using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

public sealed record DeploymentTargetDeleted(string DeploymentTargetId);

public union DeleteDeploymentTargetError(
    DeploymentTargetNotFound,
    DeploymentTargetRevisionConflict,
    DefaultDeploymentTargetDeletionRejected,
    DeploymentTargetHasDurableReferences);

public union DeleteDeploymentTargetOutcome(
    DeploymentTargetDeleted,
    DeleteDeploymentTargetError);

public delegate Task<DeleteDeploymentTargetOutcome> DeleteDeploymentTarget(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteDeploymentTarget(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    IMongoCollection<DeploymentTargetSettings> settings,
    EnsureNoDeploymentTargetReferences ensureNoDeploymentTargetReferences,
    RunMongoOutcomeTransaction<DeleteDeploymentTargetOutcome> runMongoTransaction,
    TimeProvider timeProvider)
{
    public async Task<DeleteDeploymentTargetOutcome> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        var target = await deploymentTargets
            .Find(DeploymentTargetFilters.ById(deploymentTargetId))
            .FirstOrDefaultAsync(cancellationToken);
        if (target is null)
            return Error(new DeploymentTargetNotFound(deploymentTargetId));

        var defaultId = await settings
            .Find(DeploymentTargetSettingsFilters.Singleton())
            .Project(item => item.DefaultDeploymentTargetId)
            .FirstOrDefaultAsync(cancellationToken);
        if (target.Id == defaultId)
            return Error(new DefaultDeploymentTargetDeletionRejected(target.Id));

        var fencedTarget = target;
        if (target.DeletionRequestedAtUtc is null)
        {
            var deletionRequested = target.RequestDeletion(
                DeploymentTargetClock.UtcNowAfter(timeProvider, target.UpdatedAtUtc));
            var persistedFence = await deploymentTargets.FindOneAndUpdateAsync(
                DeploymentTargetFilters.AtReferenceFenceBeforeDeletion(target),
                Builders<DeploymentTarget>.Update.Combine(
                    Builders<DeploymentTarget>.Update.Set(
                        item => item.AcceptsWorkloads,
                        deletionRequested.AcceptsWorkloads),
                    Builders<DeploymentTarget>.Update.Set(
                        item => item.DeletionRequestedAtUtc,
                        deletionRequested.DeletionRequestedAtUtc),
                    Builders<DeploymentTarget>.Update.Set(
                        item => item.Revision,
                        deletionRequested.Revision),
                    Builders<DeploymentTarget>.Update.Set(
                        item => item.UpdatedAtUtc,
                        deletionRequested.UpdatedAtUtc)),
                new FindOneAndUpdateOptions<DeploymentTarget>
                {
                    ReturnDocument = ReturnDocument.After
                },
                cancellationToken);
            if (persistedFence is null)
                return Error(new DeploymentTargetRevisionConflict(target.Id));
            fencedTarget = persistedFence;
        }

        var references = await ensureNoDeploymentTargetReferences(
            fencedTarget.Id,
            cancellationToken);
        if (references is Err<DeploymentTargetHasDurableReferences> referenceError)
            return Error(referenceError.Value);

        return await runMongoTransaction(async (transaction, ct) =>
        {
            var current = await deploymentTargets
                .Find(transaction.Session, DeploymentTargetFilters.AtDeletionFence(fencedTarget))
                .FirstOrDefaultAsync(ct);
            if (current is null)
                return transaction.Abort(Error(new DeploymentTargetRevisionConflict(fencedTarget.Id)));

            var currentDefaultId = await settings
                .Find(transaction.Session, DeploymentTargetSettingsFilters.Singleton())
                .Project(item => item.DefaultDeploymentTargetId)
                .FirstOrDefaultAsync(ct);
            if (current.Id == currentDefaultId)
            {
                return transaction.Abort(
                    Error(new DefaultDeploymentTargetDeletionRejected(current.Id)));
            }

            var deletion = await deploymentTargets.DeleteOneAsync(
                transaction.Session,
                DeploymentTargetFilters.AtDeletionFence(current),
                cancellationToken: ct);
            return deletion.DeletedCount == 1
                ? transaction.Commit(new DeploymentTargetDeleted(current.Id))
                : transaction.Abort(Error(new DeploymentTargetRevisionConflict(current.Id)));
        }, cancellationToken);
    }

    private static DeleteDeploymentTargetOutcome Error(DeleteDeploymentTargetError error) =>
        new(error);
}
