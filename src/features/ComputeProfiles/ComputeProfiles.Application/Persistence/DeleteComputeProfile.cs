using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

public delegate Task<Result> DeleteComputeProfile(
    string computeProfileId,
    long expectedRevision,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteComputeProfile(
    IMongoDatabase database,
    IMongoCollection<ComputeProfile> computeProfiles,
    IDeploymentTargetReferenceAdmission referenceAdmission,
    RunMongoTransaction runMongoTransaction)
{
    private const string ResourceDeploymentCollectionName = "ResourceDeployments";

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

            var resourceDeployments = database.GetCollection<BsonDocument>(
                ResourceDeploymentCollectionName);
            var referenced = await resourceDeployments
                .Find(
                    session,
                    Builders<BsonDocument>.Filter.Eq(
                        "ResolvedPlacement.Compute.ComputeProfileId",
                        computeProfileId))
                .Project(Builders<BsonDocument>.Projection.Include("_id"))
                .FirstOrDefaultAsync(ct);
            if (referenced is not null)
            {
                return Failures.Conflict(
                    "The compute profile is still referenced by a resource deployment.");
            }

            var deleted = await computeProfiles.DeleteOneAsync(
                session,
                ComputeProfileFilters.AtRevision(computeProfileId, expectedRevision),
                cancellationToken: ct);
            if (deleted.DeletedCount != 1)
            {
                return Failures.Conflict(
                    "The compute profile changed while deletion was being accepted.");
            }

            return await ComputeProfileTargetReferences.UnregisterAsync(
                referenceAdmission,
                profile,
                profile.TargetMappings.Select(mapping => mapping.DeploymentTargetId),
                session,
                ct);
        }, cancellationToken);
}
