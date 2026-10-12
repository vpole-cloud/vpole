using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.ComputeProfiles.Events;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Persistence.Defaults;
using Pole.Features.ComputeProfiles.Application.Persistence.References;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence.Mutations;

public union PersistUpdatedComputeProfileOutcome(
    ComputeProfile,
    ComputeProfileRevisionConflict,
    DefaultComputeProfileCannotBeDemoted,
    DeploymentTargetReferenceChangeRejected);

public delegate Task<PersistUpdatedComputeProfileOutcome>
    PersistUpdatedComputeProfile(
    ComputeProfile current,
    ComputeProfile replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles,
    ApplyDeploymentTargetReferenceChanges applyTargetReferenceChanges,
    RunMongoOutcomeTransaction<PersistUpdatedComputeProfileOutcome> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<PersistUpdatedComputeProfileOutcome> InvokeAsync(
        ComputeProfile current,
        ComputeProfile replacement,
        CancellationToken cancellationToken = default)
    {
        ComputeProfilePersistenceGuards.EnsureSameIdentity(current, replacement);
        if (current.IsDefault && !replacement.IsDefault)
        {
            return Task.FromResult<PersistUpdatedComputeProfileOutcome>(
                new DefaultComputeProfileCannotBeDemoted(current.Id));
        }

        var currentTargetIds = current.TargetMappings
            .Select(mapping => mapping.DeploymentTargetId)
            .ToHashSet(StringComparer.Ordinal);
        var replacementTargetIds = replacement.TargetMappings
            .Select(mapping => mapping.DeploymentTargetId)
            .ToHashSet(StringComparer.Ordinal);

        return runMongoTransaction(async (transaction, ct) =>
        {
            var references = await ComputeProfileTargetReferences.ApplyChangesAsync(
                applyTargetReferenceChanges,
                replacement,
                replacementTargetIds.Except(currentTargetIds, StringComparer.Ordinal),
                currentTargetIds.Except(replacementTargetIds, StringComparer.Ordinal),
                transaction.Session,
                ct);
            if (references is DeploymentTargetReferenceChangeRejected error)
            {
                return transaction.Abort(error);
            }

            if (replacement.IsDefault)
            {
                await ComputeProfileDefaultPersistence.DemoteOtherDefaultsAsync(
                    computeProfiles,
                    transaction.Session,
                    replacement.Id,
                    replacement.UpdatedAtUtc,
                    ct);
            }

            var replaced = await computeProfiles.ReplaceOneAsync(
                transaction.Session,
                ComputeProfileFilters.AtSnapshot(current),
                replacement,
                cancellationToken: ct);
            if (replaced.ModifiedCount != 1)
            {
                return transaction.Abort(new ComputeProfileRevisionConflict(current.Id));
            }

            await storeOutboxWrites(transaction.Session,
                [OutboxWrite.Create(new ComputeProfileCatalogChangedEvent(replacement.Id, replacement.Revision))], ct);
            return transaction.Commit(replacement);
        }, cancellationToken);
    }
}
