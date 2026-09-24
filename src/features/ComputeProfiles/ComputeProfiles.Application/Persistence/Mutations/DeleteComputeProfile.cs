using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ComputeProfiles.Application.Persistence.References;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence.Mutations;

public sealed record DefaultComputeProfileCannotBeDeleted(string ComputeProfileId);

public sealed record ComputeProfileDeleted(string ComputeProfileId);

public delegate Task<DeleteComputeProfileOutcome> DeleteComputeProfile(
    string computeProfileId,
    long expectedRevision,
    CancellationToken cancellationToken = default);

internal sealed class MongoDeleteComputeProfile(
    IMongoCollection<ComputeProfile> computeProfiles,
    ApplyDeploymentTargetReferenceChanges applyTargetReferenceChanges,
    EnsureComputeProfileUnreferenced ensureResourceDeploymentUnreferenced,
    RunMongoOutcomeTransaction<DeleteComputeProfileOutcome> runMongoTransaction)
{
    public Task<DeleteComputeProfileOutcome> InvokeAsync(
        string computeProfileId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (transaction, ct) =>
        {
            var profile = await computeProfiles
                .Find(transaction.Session, ComputeProfileFilters.ById(computeProfileId))
                .FirstOrDefaultAsync(ct);
            if (profile is null)
            {
                return transaction.Abort(
                    new ComputeProfileNotFound(computeProfileId));
            }
            if (profile.Revision != expectedRevision)
            {
                return transaction.Abort(
                    new ComputeProfileRevisionConflict(computeProfileId));
            }
            if (profile.IsDefault)
            {
                return transaction.Abort(
                    new DefaultComputeProfileCannotBeDeleted(computeProfileId));
            }

            var unreferenced = await ensureResourceDeploymentUnreferenced(
                computeProfileId,
                transaction.Session,
                ct);
            if (!unreferenced.TryGetValue(out _, out var referenceError))
            {
                return transaction.Abort(referenceError);
            }

            var deleted = await computeProfiles.DeleteOneAsync(
                transaction.Session,
                ComputeProfileFilters.AtRevision(computeProfileId, expectedRevision),
                cancellationToken: ct);
            if (deleted.DeletedCount != 1)
            {
                return transaction.Abort(
                    new ComputeProfileRevisionConflict(computeProfileId));
            }

            var references = await ComputeProfileTargetReferences.ApplyChangesAsync(
                applyTargetReferenceChanges,
                profile,
                [],
                profile.TargetMappings.Select(mapping => mapping.DeploymentTargetId),
                transaction.Session,
                ct);
            return references switch
            {
                Ok<Unit> => transaction.Commit(new ComputeProfileDeleted(computeProfileId)),
                Err<DeploymentTargetReferenceChangeRejected> error => transaction.Abort(
                    error.Value)
            };
        }, cancellationToken);

}
