using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application.Persistence.Defaults;
using Pole.Features.ComputeProfiles.Application.Persistence.References;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence.Mutations;

public union PersistCreatedComputeProfileOutcome(
    ComputeProfile,
    DeploymentTargetReferenceChangeRejected);

public delegate Task<PersistCreatedComputeProfileOutcome>
    PersistCreatedComputeProfile(
    ComputeProfile profile,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles,
    ApplyDeploymentTargetReferenceChanges applyTargetReferenceChanges,
    RunMongoOutcomeTransaction<PersistCreatedComputeProfileOutcome> runMongoTransaction)
{
    public Task<PersistCreatedComputeProfileOutcome> InvokeAsync(
        ComputeProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return runMongoTransaction(async (transaction, ct) =>
        {
            var references = await ComputeProfileTargetReferences.ApplyChangesAsync(
                applyTargetReferenceChanges,
                profile,
                profile.TargetMappings.Select(mapping => mapping.DeploymentTargetId),
                [],
                transaction.Session,
                ct);
            if (references is Err<DeploymentTargetReferenceChangeRejected> error)
            {
                return transaction.Abort(error.Value);
            }

            if (profile.IsDefault)
            {
                await ComputeProfileDefaultPersistence.DemoteOtherDefaultsAsync(
                    computeProfiles,
                    transaction.Session,
                    exceptProfileId: null,
                    profile.UpdatedAtUtc,
                    ct);
            }

            await computeProfiles.InsertOneAsync(
                transaction.Session,
                profile,
                cancellationToken: ct);
            return transaction.Commit(profile);
        }, cancellationToken);
    }
}
