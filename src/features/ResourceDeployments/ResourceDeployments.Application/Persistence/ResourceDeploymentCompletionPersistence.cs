using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

internal sealed class ResourceDeploymentCompletionPersistence(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    RunMongoTransaction runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<Result> PersistAsync(
        ResourceDeployment claimed,
        Func<ResourceDeployment, ResourceDeployment> transition,
        CancellationToken cancellationToken) =>
        runMongoTransaction(async (session, ct) =>
        {
            var current = await resourceDeployments
                .Find(
                    session,
                    ResourceDeploymentFilters.ForTenantDeployment(
                        claimed.TenantId,
                        claimed.Id))
                .FirstOrDefaultAsync(ct);
            if (current is null ||
                current.State is not ReconcilingResourceDeploymentState activeClaim ||
                claimed.State is not ReconcilingResourceDeploymentState expectedClaim ||
                !string.Equals(
                    activeClaim.ClaimId,
                    expectedClaim.ClaimId,
                    StringComparison.Ordinal))
            {
                return Result.Success();
            }

            var completed = transition(current);
            var replacement = await resourceDeployments.ReplaceOneAsync(
                session,
                ResourceDeploymentFilters.ForActiveClaimAtRevision(
                    current,
                    activeClaim.ClaimId),
                completed,
                cancellationToken: ct);
            if (replacement.ModifiedCount != 1)
                return Result.Success();

            await storeOutboxWrites(
                session,
                [ToCompletionWrite(completed)],
                ct);
            return Result.Success();
        }, cancellationToken);

    public static ReconcilingResourceDeploymentState GetActiveClaim(
        ResourceDeployment claimed) =>
        claimed.State as ReconcilingResourceDeploymentState
        ?? throw new InvalidOperationException(
            "The resource deployment has no active claim.");

    private static OutboxWrite ToCompletionWrite(
        ResourceDeployment completed) => completed.State switch
        {
            PendingResourceDeploymentState => completed.ToCurrentRequestWrite(),
            AppliedResourceDeploymentState applied =>
                OutboxWrite.Create(completed.ToAppliedEvent(applied.Generation)),
            FailedResourceDeploymentState failed =>
                OutboxWrite.Create(completed.ToFailedEvent(failed)),
            DeletedResourceDeploymentState deleted =>
                OutboxWrite.Create(completed.ToDeletedEvent(deleted.Generation)),
            _ => throw new InvalidOperationException(
                $"Unsupported resource deployment completion state '{completed.State.GetType().Name}'.")
        };
}
