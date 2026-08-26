using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Persistence.Defaults;
using Pole.Features.ComputeProfiles.Application.Persistence.References;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence.Mutations;

public union PersistUpdatedComputeProfileError(
    ComputeProfileRevisionConflict,
    DefaultComputeProfileCannotBeDemoted,
    DeploymentTargetReferenceChangeRejected);

public delegate Task<Result<ComputeProfile, PersistUpdatedComputeProfileError>>
    PersistUpdatedComputeProfile(
    ComputeProfile current,
    ComputeProfile replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles,
    ApplyDeploymentTargetReferenceChanges applyTargetReferenceChanges,
    RunMongoOutcomeTransaction<Result<
        ComputeProfile,
        PersistUpdatedComputeProfileError>> runMongoTransaction)
{
    public Task<Result<ComputeProfile, PersistUpdatedComputeProfileError>> InvokeAsync(
        ComputeProfile current,
        ComputeProfile replacement,
        CancellationToken cancellationToken = default)
    {
        ComputeProfilePersistenceGuards.EnsureSameIdentity(current, replacement);
        if (current.IsDefault && !replacement.IsDefault)
        {
            return Task.FromResult<Result<ComputeProfile, PersistUpdatedComputeProfileError>>(
                new Err<PersistUpdatedComputeProfileError>(
                    new PersistUpdatedComputeProfileError(
                        new DefaultComputeProfileCannotBeDemoted(current.Id))));
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
            if (references is Err<DeploymentTargetReferenceChangeRejected> error)
            {
                return transaction.AbortError(new(error.Value));
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
                return transaction.AbortError(
                    new(new ComputeProfileRevisionConflict(current.Id)));
            }

            return transaction.Commit(new Ok<ComputeProfile>(replacement));
        }, cancellationToken);
    }
}
