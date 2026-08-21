using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Persistence;

public sealed record DefaultComputeProfileCannotBeDeleted(string ComputeProfileId);

public union DeleteComputeProfileStateError(
    ComputeProfileNotFound,
    ComputeProfileRevisionConflict,
    DefaultComputeProfileCannotBeDeleted,
    DeploymentTargetReferenceChangeRejected);

public union DeleteComputeProfileError(
    DeleteComputeProfileStateError,
    ComputeProfileReferencedByResourceDeployment);

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
                return transaction.Abort(StateError(
                    new ComputeProfileNotFound(computeProfileId)));
            }
            if (profile.Revision != expectedRevision)
            {
                return transaction.Abort(StateError(
                    new ComputeProfileRevisionConflict(computeProfileId)));
            }
            if (profile.IsDefault)
            {
                return transaction.Abort(StateError(
                    new DefaultComputeProfileCannotBeDeleted(computeProfileId)));
            }

            var unreferenced = await ensureResourceDeploymentUnreferenced(
                computeProfileId,
                transaction.Session,
                ct);
            if (!unreferenced.TryGetValue(out _, out var referenceError))
            {
                return transaction.Abort(ProviderError(referenceError));
            }

            var deleted = await computeProfiles.DeleteOneAsync(
                transaction.Session,
                ComputeProfileFilters.AtRevision(computeProfileId, expectedRevision),
                cancellationToken: ct);
            if (deleted.DeletedCount != 1)
            {
                return transaction.Abort(StateError(
                    new ComputeProfileRevisionConflict(computeProfileId)));
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
                    StateError(error.Value))
            };
        }, cancellationToken);

    private static DeleteComputeProfileOutcome StateError(
        DeleteComputeProfileStateError error) =>
        new(new DeleteComputeProfileError(error));

    private static DeleteComputeProfileOutcome ProviderError(
        ComputeProfileReferencedByResourceDeployment error) =>
        new(new DeleteComputeProfileError(error));
}
