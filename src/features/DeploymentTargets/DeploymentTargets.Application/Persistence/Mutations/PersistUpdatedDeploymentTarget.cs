using MongoDB.Driver;
using MongoDB.Bson;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.DeploymentTargets.Events;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Persistence.References;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Mutations;

public union PersistUpdatedDeploymentTargetError(
    DeploymentTargetRevisionConflict,
    DeploymentTargetPublicBaseDomainReferenced,
    DeploymentTargetPublicBaseDomainConflict);

public union PersistUpdatedDeploymentTargetOutcome(
    DeploymentTarget,
    PersistUpdatedDeploymentTargetError);

public delegate Task<PersistUpdatedDeploymentTargetOutcome>
    PersistUpdatedDeploymentTarget(
        DeploymentTarget current,
        DeploymentTarget replacement,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedDeploymentTarget(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    EnsureNoDeploymentTargetPublicBaseDomainReferences ensureNoPublicBaseDomainReferences,
    EnsureNoOverlappingDeploymentTargetPublicBaseDomain ensureNoOverlappingPublicBaseDomain,
    RunMongoOutcomeTransaction<PersistUpdatedDeploymentTargetOutcome> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<PersistUpdatedDeploymentTargetOutcome> InvokeAsync(
        DeploymentTarget current,
        DeploymentTarget replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(current.Id, replacement.Id, StringComparison.Ordinal) ||
            current.TechnicalName != replacement.TechnicalName)
        {
            throw new ArgumentException(
                "The current and replacement deployment targets must have the same ID.",
                nameof(replacement));
        }

        var publicBaseDomainChanged = !string.Equals(
            current.WorkloadConfiguration.Ingress?.PublicBaseDomain,
            replacement.WorkloadConfiguration.Ingress?.PublicBaseDomain,
            StringComparison.Ordinal);
        return await runMongoTransaction(async (transaction, ct) =>
            {
                if (publicBaseDomainChanged)
                {
                    var references = await ensureNoPublicBaseDomainReferences(
                        current.Id,
                        transaction.Session,
                        ct);
                    if (references is DeploymentTargetPublicBaseDomainReferenced referenceError)
                        return transaction.Abort(new PersistUpdatedDeploymentTargetError(referenceError));
                    var overlap = await ensureNoOverlappingPublicBaseDomain(
                        current.Id,
                        replacement.WorkloadConfiguration.Ingress?.PublicBaseDomain,
                        transaction.Session,
                        ct);
                    if (overlap is DeploymentTargetPublicBaseDomainConflict overlapError)
                        return transaction.Abort(new PersistUpdatedDeploymentTargetError(overlapError));
                }

                var filter = publicBaseDomainChanged
                    ? DeploymentTargetFilters.AtRevisionAndReferenceFence(current)
                    : DeploymentTargetFilters.AtRevision(current);
                var before = await deploymentTargets.Find(transaction.Session, filter).FirstOrDefaultAsync(ct);
                if (before is null)
                    return transaction.Abort(
                        new PersistUpdatedDeploymentTargetError(new DeploymentTargetRevisionConflict(current.Id)));
                var persisted = await deploymentTargets.FindOneAndUpdateAsync(
                    transaction.Session,
                    filter,
                    ReplacementUpdate(replacement),
                    new FindOneAndUpdateOptions<DeploymentTarget> { ReturnDocument = ReturnDocument.After },
                    ct);
                if (persisted is null)
                    return transaction.Abort(
                        new PersistUpdatedDeploymentTargetError(new DeploymentTargetRevisionConflict(current.Id)));
                if (MetadataChanged(before, persisted))
                    await storeOutboxWrites(transaction.Session,
                        [OutboxWrite.Create(new DeploymentTargetCatalogChangedEvent(persisted.Id, persisted.Revision))],
                        ct);
                return transaction.Commit(persisted);
            },
            cancellationToken);
    }

    private static bool MetadataChanged(DeploymentTarget before, DeploymentTarget after) =>
        before.DisplayName != after.DisplayName || before.Enabled != after.Enabled ||
        before.AcceptsWorkloads != after.AcceptsWorkloads || before.Revision != after.Revision ||
        before.UpdatedAtUtc != after.UpdatedAtUtc ||
        !before.WorkloadConfiguration.ToBsonDocument().Equals(after.WorkloadConfiguration.ToBsonDocument());

    private static UpdateDefinition<DeploymentTarget> ReplacementUpdate(
        DeploymentTarget replacement) => Builders<DeploymentTarget>.Update.Combine(
        Builders<DeploymentTarget>.Update.Set(item => item.DisplayName, replacement.DisplayName),
        Builders<DeploymentTarget>.Update.Set(item => item.Enabled, replacement.Enabled),
        Builders<DeploymentTarget>.Update.Set(
            item => item.AcceptsWorkloads,
            replacement.AcceptsWorkloads),
        Builders<DeploymentTarget>.Update.Set(
            item => item.WorkloadConfiguration,
            replacement.WorkloadConfiguration),
        Builders<DeploymentTarget>.Update.Set(item => item.Revision, replacement.Revision),
        Builders<DeploymentTarget>.Update.Set(item => item.UpdatedAtUtc, replacement.UpdatedAtUtc));
}
