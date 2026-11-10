using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Defaults;
using Pole.Features.ComputeProfiles.Application.Mongo;
using Pole.Features.ComputeProfiles.Application.Resolution;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.ComputeProfiles.Events;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.ComputeProfiles.Application.Catalog;

public union PersistUpdatedComputeProfileOutcome(
    ComputeProfile,
    ComputeProfileRevisionConflict,
    DefaultComputeProfileCannotBeDemoted,
    DeploymentTargetReferenceChangeRejected);

public delegate Task<PersistUpdatedComputeProfileOutcome> PersistUpdatedComputeProfile(
    ComputeProfile current,
    ComputeProfile replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles,
    ApplyDeploymentTargetReferenceChanges applyTargetReferenceChanges,
    RunMongoOutcomeTransaction<PersistUpdatedComputeProfileOutcome> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites,
    ComputeProfileHistory history
)
{
    public Task<PersistUpdatedComputeProfileOutcome> InvokeAsync(
        ComputeProfile current,
        ComputeProfile replacement,
        CancellationToken cancellationToken = default)
    {
        ComputeProfilePersistenceGuards.EnsureSameIdentity(current, replacement);
        if (current.IsDefault && !replacement.IsDefault)
            return Task.FromResult<PersistUpdatedComputeProfileOutcome>(
                new DefaultComputeProfileCannotBeDemoted(current.Id));

        var currentTargetIds = current.TargetMappings
            .Select(mapping => mapping.DeploymentTargetId)
            .ToHashSet(StringComparer.Ordinal);
        var replacementTargetIds = replacement.TargetMappings
            .Select(mapping => mapping.DeploymentTargetId)
            .ToHashSet(StringComparer.Ordinal);

        return runMongoTransaction(
            async (transaction, ct) =>
            {
                var references = await ComputeProfileTargetReferences.ApplyChangesAsync(
                    applyTargetReferenceChanges,
                    replacement,
                    replacementTargetIds.Except(currentTargetIds, StringComparer.Ordinal),
                    currentTargetIds.Except(replacementTargetIds, StringComparer.Ordinal),
                    transaction.Session,
                    ct);
                if (references is DeploymentTargetReferenceChangeRejected error)
                    return transaction.Abort(error);

                if (replacement.IsDefault)
                {
                    var demoted = await ComputeProfileDefaultPersistence.DemoteOtherDefaultsAsync(
                        computeProfiles,
                        history,
                        transaction.Session,
                        replacement.Id,
                        replacement.UpdatedAtUtc,
                        ct);
                    if (demoted is ComputeProfileRevisionConflict conflict)
                        return transaction.Abort(conflict);
                }

                var replaced = await computeProfiles.ReplaceOneAsync(
                    transaction.Session,
                    ComputeProfileFilters.AtSnapshot(current),
                    replacement,
                    cancellationToken: ct);
                if (replaced.ModifiedCount != 1)
                    return transaction.Abort(new ComputeProfileRevisionConflict(current.Id));

                await history.StoreAsync(replacement, transaction.Session, ct);
                await history.StoreAsync(current, transaction.Session, ct);
                await storeOutboxWrites(
                    transaction.Session,
                    [OutboxWrite.Create(new ComputeProfileCatalogChangedEvent(replacement.Id, replacement.Revision))],
                    ct);
                return transaction.Commit(replacement);
            },
            cancellationToken);
    }
}
