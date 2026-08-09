using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

public delegate Task<Result<ComputeProfile>> PersistCreatedComputeProfile(
    ComputeProfile profile,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles,
    IDeploymentTargetReferenceAdmission referenceAdmission,
    RunMongoTransaction<ComputeProfile> runMongoTransaction)
{
    public Task<Result<ComputeProfile>> InvokeAsync(
        ComputeProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return runMongoTransaction(async (session, ct) =>
        {
            var references = await ComputeProfileTargetReferences.ApplyChangesAsync(
                referenceAdmission,
                profile,
                profile.TargetMappings.Select(mapping => mapping.DeploymentTargetId),
                [],
                session,
                ct);
            if (references is not ISuccess)
                return (Failure)references;

            if (profile.IsDefault)
            {
                await ComputeProfileDefaultPersistence.DemoteOtherDefaultsAsync(
                    computeProfiles,
                    session,
                    exceptProfileId: null,
                    profile.UpdatedAtUtc,
                    ct);
            }

            await computeProfiles.InsertOneAsync(
                session,
                profile,
                cancellationToken: ct);
            return profile;
        }, cancellationToken);
    }
}
