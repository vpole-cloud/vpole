// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Reconciliation;

internal sealed class ResourceDeploymentCompletionPersistence(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    RunMongoOutcomeTransaction<Unit> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites
)
{
    public Task PersistAsync(
        ResourceDeployment claimed,
        Func<ResourceDeployment, ResourceDeployment> transition,
        CancellationToken cancellationToken) => runMongoTransaction(
        async (transaction, ct) =>
        {
            var current = await resourceDeployments.Find(
                    transaction.Session,
                    ResourceDeploymentFilters.ForTenantDeployment(claimed.TenantId, claimed.Id))
                .FirstOrDefaultAsync(ct);
            if (current is null || current.State is not ReconcilingResourceDeploymentState activeClaim ||
                claimed.State is not ReconcilingResourceDeploymentState expectedClaim || !string.Equals(
                    activeClaim.ClaimId,
                    expectedClaim.ClaimId,
                    StringComparison.Ordinal))
                return transaction.Commit(Unit.Value);

            var completed = transition(current);
            var replacement = await resourceDeployments.ReplaceOneAsync(
                transaction.Session,
                ResourceDeploymentFilters.ForActiveClaimAtRevision(current, activeClaim.ClaimId),
                completed,
                cancellationToken: ct);
            if (replacement.ModifiedCount != 1)
                return transaction.Commit(Unit.Value);

            await storeOutboxWrites(transaction.Session, [ToCompletionWrite(completed)], ct);
            return transaction.Commit(Unit.Value);
        },
        cancellationToken);

    public static ReconcilingResourceDeploymentState GetActiveClaim(ResourceDeployment claimed) =>
        claimed.State as ReconcilingResourceDeploymentState ??
        throw new InvalidOperationException("The resource deployment has no active claim.");

    private static OutboxWrite ToCompletionWrite(ResourceDeployment completed) => completed.State switch
    {
        PendingResourceDeploymentState => completed.ToCurrentRequestWrite(),
        AppliedResourceDeploymentState applied => OutboxWrite.Create(completed.ToAppliedEvent(applied.Generation)),
        FailedResourceDeploymentState failed => OutboxWrite.Create(completed.ToFailedEvent(failed)),
        DeletedResourceDeploymentState deleted => OutboxWrite.Create(completed.ToDeletedEvent(deleted.Generation)),
        _ => throw new InvalidOperationException(
            $"Unsupported resource deployment completion state '{completed.State.GetType().Name}'."),
    };
}
