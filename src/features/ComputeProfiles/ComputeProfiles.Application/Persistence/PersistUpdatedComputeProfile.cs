using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

public delegate Task<Result<ComputeProfile>> PersistUpdatedComputeProfile(
    ComputeProfile current,
    ComputeProfile replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles,
    IDeploymentTargetReferenceAdmission referenceAdmission,
    RunMongoTransaction<ComputeProfile> runMongoTransaction)
{
    public Task<Result<ComputeProfile>> InvokeAsync(
        ComputeProfile current,
        ComputeProfile replacement,
        CancellationToken cancellationToken = default)
    {
        ComputeProfilePersistenceGuards.EnsureSameIdentity(current, replacement);
        if (current.IsDefault && !replacement.IsDefault)
        {
            return Task.FromResult<Result<ComputeProfile>>(Failures.Conflict(
                "Promote another compute profile before demoting the current default."));
        }

        var currentTargetIds = current.TargetMappings
            .Select(mapping => mapping.DeploymentTargetId)
            .ToHashSet(StringComparer.Ordinal);
        var replacementTargetIds = replacement.TargetMappings
            .Select(mapping => mapping.DeploymentTargetId)
            .ToHashSet(StringComparer.Ordinal);

        return runMongoTransaction(async (session, ct) =>
        {
            var registered = await ComputeProfileTargetReferences.RegisterAsync(
                referenceAdmission,
                replacement,
                replacementTargetIds.Except(currentTargetIds, StringComparer.Ordinal),
                session,
                ct);
            if (registered is not ISuccess)
                return (Failure)registered;

            if (replacement.IsDefault)
            {
                await ComputeProfileDefaultPersistence.DemoteOtherDefaultsAsync(
                    computeProfiles,
                    session,
                    replacement.Id,
                    replacement.UpdatedAtUtc,
                    ct);
            }

            var replaced = await computeProfiles.ReplaceOneAsync(
                session,
                ComputeProfileFilters.AtSnapshot(current),
                replacement,
                cancellationToken: ct);
            if (replaced.ModifiedCount != 1)
            {
                return Failures.Conflict(
                    "The compute profile changed while the update was being accepted.");
            }

            var unregistered = await ComputeProfileTargetReferences.UnregisterAsync(
                referenceAdmission,
                current,
                currentTargetIds.Except(replacementTargetIds, StringComparer.Ordinal),
                session,
                ct);
            return unregistered is ISuccess
                ? replacement
                : (Failure)unregistered;
        }, cancellationToken);
    }
}
