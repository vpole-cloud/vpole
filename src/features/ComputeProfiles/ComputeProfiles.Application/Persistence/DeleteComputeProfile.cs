using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

public delegate Task<Result> DeleteComputeProfile(
    string computeProfileId,
    long expectedRevision,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles,
    IDeploymentTargetReferenceAdmission referenceAdmission,
    IResourceDeploymentComputeProfileReferenceGuard resourceDeploymentReferences,
    RunMongoTransaction runMongoTransaction)
{
    public Task<Result> InvokeAsync(
        string computeProfileId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (session, ct) =>
        {
            var profile = await computeProfiles
                .Find(session, ComputeProfileFilters.ById(computeProfileId))
                .FirstOrDefaultAsync(ct);
            if (profile is null)
            {
                return Failures.NotFound(
                    "Compute profile not found.",
                    computeProfileId);
            }
            if (profile.Revision != expectedRevision)
            {
                return Failures.Conflict(
                    "The compute profile changed. Reload it and retry.");
            }
            if (profile.IsDefault)
            {
                return Failures.Conflict(
                    "Promote another compute profile before deleting the current default.");
            }

            var unreferenced = await resourceDeploymentReferences
                .EnsureUnreferencedAsync(computeProfileId, session, ct);
            if (unreferenced is not ISuccess)
                return (Failure)unreferenced;

            var deleted = await computeProfiles.DeleteOneAsync(
                session,
                ComputeProfileFilters.AtRevision(computeProfileId, expectedRevision),
                cancellationToken: ct);
            if (deleted.DeletedCount != 1)
            {
                return Failures.Conflict(
                    "The compute profile changed while deletion was being accepted.");
            }

            return await ComputeProfileTargetReferences.ApplyChangesAsync(
                referenceAdmission,
                profile,
                [],
                profile.TargetMappings.Select(mapping => mapping.DeploymentTargetId),
                session,
                ct);
        }, cancellationToken);
}
