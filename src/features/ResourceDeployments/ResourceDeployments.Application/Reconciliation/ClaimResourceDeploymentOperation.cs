using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Reconciliation;

public delegate Task<ResourceDeployment?> ClaimResourceDeployment(
    string tenantId,
    string deploymentId,
    string runtimeCellId,
    long generation,
    ResourceDeploymentOperation operation,
    CancellationToken cancellationToken = default);

internal sealed record ResourceDeploymentNotClaimed;

internal union ClaimResourceDeploymentOutcome(
    ResourceDeployment,
    ResourceDeploymentNotClaimed);

internal sealed class ClaimResourceDeploymentOperation(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    ResourceDeploymentSettings settings,
    TimeProvider timeProvider,
    MongoRunTransaction transactions,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<ResourceDeployment?> InvokeAsync(
        string tenantId,
        string deploymentId,
        string runtimeCellId,
        long generation,
        ResourceDeploymentOperation operation,
        CancellationToken cancellationToken = default)
    {
        var outcome = await transactions.InvokeOutcomeAsync<ClaimResourceDeploymentOutcome>(async (transaction, ct) =>
            {
                var current = await resourceDeployments
                    .Find(transaction.Session,
                        ResourceDeploymentFilters.ForPendingReconciliation(
                            tenantId,
                            deploymentId,
                            runtimeCellId,
                            generation,
                            operation))
                    .FirstOrDefaultAsync(ct);
                if (current is null || current.State is not PendingResourceDeploymentState pending ||
                    pending.Generation != generation || pending.Operation != operation)
                {
                    ClaimResourceDeploymentOutcome rejected = new ResourceDeploymentNotClaimed();
                    return transaction.Commit(rejected);
                }

                var startedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                var claimed = current.Start(generation,
                    operation,
                    ObjectId.GenerateNewId().ToString(),
                    startedAtUtc,
                    startedAtUtc.AddSeconds(settings.ClaimLeaseSeconds));
                var replacement = await resourceDeployments.ReplaceOneAsync(
                    transaction.Session,
                    ResourceDeploymentFilters.ForPendingAtRevision(current),
                    claimed,
                    cancellationToken: ct);
                if (replacement.ModifiedCount != 1)
                {
                    ClaimResourceDeploymentOutcome rejected = new ResourceDeploymentNotClaimed();
                    return transaction.Commit(rejected);
                }

                await storeOutboxWrites(transaction.Session, [OutboxWrite.Create(claimed.ToStartedEvent())], ct);
                ClaimResourceDeploymentOutcome accepted = claimed;
                return transaction.Commit(accepted);
            },
            cancellationToken);
        return outcome switch
        {
            ResourceDeployment claimed => claimed,
            ResourceDeploymentNotClaimed => null
        };
    }
}
