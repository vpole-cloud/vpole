using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.DeploymentTargets.Events;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence.References;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Mutations;

public sealed record DeploymentTargetDeleted(string DeploymentTargetId);

internal union PersistDeploymentTargetDeletionFenceOutcome(
    DeploymentTarget,
    DeploymentTargetRevisionConflict);

public union DeleteDeploymentTargetError(
    DeploymentTargetNotFound,
    DeploymentTargetRevisionConflict,
    DefaultDeploymentTargetDeletionRejected,
    DeploymentTargetHasDurableReferences);

public union DeleteDeploymentTargetPersistenceOutcome(
    DeploymentTargetDeleted,
    DeleteDeploymentTargetError);

public delegate Task<DeleteDeploymentTargetPersistenceOutcome> DeleteDeploymentTarget(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteDeploymentTarget(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    IMongoCollection<DeploymentTargetSettings> settings,
    EnsureNoDeploymentTargetReferences ensureNoDeploymentTargetReferences,
    RunMongoOutcomeTransaction<DeleteDeploymentTargetPersistenceOutcome> runMongoTransaction,
    TimeProvider timeProvider,
    RunMongoOutcomeTransaction<PersistDeploymentTargetDeletionFenceOutcome> runFenceTransaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<DeleteDeploymentTargetPersistenceOutcome> InvokeAsync(
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
            var fenceOutcome = await runFenceTransaction(async (transaction, ct) =>
                {
                    var persistedFence = await deploymentTargets.FindOneAndUpdateAsync(
                        transaction.Session,
                        DeploymentTargetFilters.AtReferenceFenceBeforeDeletion(target),
                        Builders<DeploymentTarget>.Update.Combine(
                            Builders<DeploymentTarget>.Update.Set(item => item.AcceptsWorkloads,
                                deletionRequested.AcceptsWorkloads),
                            Builders<DeploymentTarget>.Update.Set(item => item.DeletionRequestedAtUtc,
                                deletionRequested.DeletionRequestedAtUtc),
                            Builders<DeploymentTarget>.Update.Set(item => item.Revision, deletionRequested.Revision),
                            Builders<DeploymentTarget>.Update.Set(item => item.UpdatedAtUtc,
                                deletionRequested.UpdatedAtUtc)),
                        new FindOneAndUpdateOptions<DeploymentTarget> { ReturnDocument = ReturnDocument.After },
                        ct);
                    if (persistedFence is null)
                        return transaction.Abort(new DeploymentTargetRevisionConflict(target.Id));
                    await storeOutboxWrites(transaction.Session,
                        [
                            OutboxWrite.Create(
                                new DeploymentTargetCatalogChangedEvent(persistedFence.Id, persistedFence.Revision))
                        ],
                        ct);
                    return transaction.Commit(persistedFence);
                },
                cancellationToken);
            if (fenceOutcome is DeploymentTargetRevisionConflict conflict)
                return Error(conflict);
            fencedTarget = (DeploymentTarget)fenceOutcome.Value;
        }

        var references = await ensureNoDeploymentTargetReferences(
            fencedTarget.Id,
            cancellationToken);
        if (references is DeploymentTargetHasDurableReferences referenceError)
            return Error(referenceError);

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
                if (deletion.DeletedCount != 1)
                    return transaction.Abort(Error(new DeploymentTargetRevisionConflict(current.Id)));
                await storeOutboxWrites(transaction.Session,
                    [OutboxWrite.Create(new DeploymentTargetCatalogChangedEvent(current.Id, current.Revision))],
                    ct);
                return transaction.Commit(new DeploymentTargetDeleted(current.Id));
            },
            cancellationToken);
    }

    private static DeleteDeploymentTargetPersistenceOutcome Error(DeleteDeploymentTargetError error) => new(error);
}
