using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Domain;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Persistence;

public union PersistUpdatedDeploymentTargetError(
    DeploymentTargetRevisionConflict,
    DeploymentTargetPublicBaseDomainReferenced,
    DeploymentTargetPublicBaseDomainConflict);

public delegate Task<Result<DeploymentTarget, PersistUpdatedDeploymentTargetError>>
    PersistUpdatedDeploymentTarget(
        DeploymentTarget current,
        DeploymentTarget replacement,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistUpdatedDeploymentTarget(
    IMongoCollection<DeploymentTarget> deploymentTargets,
    EnsureNoDeploymentTargetPublicBaseDomainReferences ensureNoPublicBaseDomainReferences,
    EnsureNoOverlappingDeploymentTargetPublicBaseDomain ensureNoOverlappingPublicBaseDomain,
    RunMongoOutcomeTransaction<Result<
        DeploymentTarget,
        PersistUpdatedDeploymentTargetError>> runMongoTransaction)
{
    public async Task<Result<DeploymentTarget, PersistUpdatedDeploymentTargetError>> InvokeAsync(
        DeploymentTarget current,
        DeploymentTarget replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!string.Equals(current.Id, replacement.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The current and replacement deployment targets must have the same ID.",
                nameof(replacement));
        }

        var publicBaseDomainChanged = !string.Equals(
            current.WorkloadConfiguration.Ingress?.PublicBaseDomain,
            replacement.WorkloadConfiguration.Ingress?.PublicBaseDomain,
            StringComparison.Ordinal);
        if (!publicBaseDomainChanged)
        {
            var persisted = await deploymentTargets.FindOneAndUpdateAsync(
                DeploymentTargetFilters.AtRevision(current),
                ReplacementUpdate(replacement),
                new FindOneAndUpdateOptions<DeploymentTarget>
                {
                    ReturnDocument = ReturnDocument.After
                },
                cancellationToken);
            return persisted is null
                ? new Err<PersistUpdatedDeploymentTargetError>(
                    new PersistUpdatedDeploymentTargetError(
                        new DeploymentTargetRevisionConflict(current.Id)))
                : new Ok<DeploymentTarget>(persisted);
        }

        return await runMongoTransaction(async (transaction, ct) =>
        {
            var references = await ensureNoPublicBaseDomainReferences(
                current.Id,
                transaction.Session,
                ct);
            if (!references.TryGetValue(out _, out var referenceError))
            {
                return transaction.AbortError(
                    new PersistUpdatedDeploymentTargetError(referenceError));
            }

            var overlap = await ensureNoOverlappingPublicBaseDomain(
                current.Id,
                replacement.WorkloadConfiguration.Ingress?.PublicBaseDomain,
                transaction.Session,
                ct);
            if (!overlap.TryGetValue(out _, out var overlapError))
            {
                return transaction.AbortError(
                    new PersistUpdatedDeploymentTargetError(overlapError));
            }

            var persisted = await deploymentTargets.FindOneAndUpdateAsync(
                transaction.Session,
                DeploymentTargetFilters.AtRevisionAndReferenceFence(current),
                ReplacementUpdate(replacement),
                new FindOneAndUpdateOptions<DeploymentTarget>
                {
                    ReturnDocument = ReturnDocument.After
                },
                ct);
            return persisted is null
                ? transaction.AbortError(
                    new PersistUpdatedDeploymentTargetError(
                        new DeploymentTargetRevisionConflict(current.Id)))
                : transaction.Commit(new Ok<DeploymentTarget>(persisted));
        }, cancellationToken);
    }

    private static UpdateDefinition<DeploymentTarget> ReplacementUpdate(
        DeploymentTarget replacement) =>
        Builders<DeploymentTarget>.Update.Combine(
            Builders<DeploymentTarget>.Update.Set(item => item.DisplayName, replacement.DisplayName),
            Builders<DeploymentTarget>.Update.Set(item => item.Enabled, replacement.Enabled),
            Builders<DeploymentTarget>.Update.Set(
                item => item.AcceptsWorkloads,
                replacement.AcceptsWorkloads),
            Builders<DeploymentTarget>.Update.Set(
                item => item.WorkloadConfiguration,
                replacement.WorkloadConfiguration),
            Builders<DeploymentTarget>.Update.Set(item => item.Revision, replacement.Revision),
            Builders<DeploymentTarget>.Update.Set(item => item.UpdatedAtUtc, replacement.UpdatedAtUtc));
}
