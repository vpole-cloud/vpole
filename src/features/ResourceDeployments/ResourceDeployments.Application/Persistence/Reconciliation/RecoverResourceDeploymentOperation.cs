using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reconciliation;

public delegate Task<long> RecoverResourceDeployment(
    string deploymentId,
    DateTime now,
    CancellationToken cancellationToken = default);

internal sealed class RecoverResourceDeploymentOperation(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    RunMongoOutcomeTransaction<long> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites,
    LoadResourcePlacementInTransaction loadResourcePlacement)
{
    public Task<long> InvokeAsync(
        string deploymentId,
        DateTime now,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (transaction, ct) =>
        {
            var current = await resourceDeployments
                .Find(
                    transaction.Session,
                    ResourceDeploymentFilters.ByDeploymentId(deploymentId))
                .FirstOrDefaultAsync(ct);
            if (current is null)
                return transaction.Commit(0L);

            switch (current.State)
            {
                case AppliedResourceDeploymentState when
                    current.DesiredOperation == ResourceDeploymentOperation.Apply &&
                    current.UpdatedAtUtc <= ResourceDeploymentVerificationSchedule.DueBefore(now):
                    return transaction.Commit(await PersistRecoveryAsync(
                        current,
                        current.RequestVerification(now),
                        transaction.Session,
                        ct));

                case PendingResourceDeploymentState:
                    await storeOutboxWrites(
                        transaction.Session,
                        [current.ToCurrentRequestWrite()],
                        ct);
                    return transaction.Commit(0L);

                case ReconcilingResourceDeploymentState
                {
                    LeaseExpiresAtUtc: var leaseExpiresAtUtc
                } when leaseExpiresAtUtc <= now:
                    return transaction.Commit(await PersistRecoveryAsync(
                        current,
                        current.RecoverStaleClaim(now),
                        transaction.Session,
                        ct,
                        staleClaimRecoveryCount: 1));

                case FailedResourceDeploymentState
                {
                    Retryable: true,
                    RetryNotBeforeUtc: var retryAtUtc
                } when retryAtUtc <= now:
                    return transaction.Commit(await PersistRecoveryAsync(
                        current,
                        current.Retry(now),
                        transaction.Session,
                        ct));

                case WaitingForRuntimeCellResourceDeploymentState:
                    var runtimeCell = await loadResourcePlacement(
                        current.TenantId,
                        current.RuntimeCellId,
                        transaction.Session,
                        ct);
                    if (runtimeCell?.State != ResourcePlacementState.Ready)
                        return transaction.Commit(0L);

                    return transaction.Commit(await PersistRecoveryAsync(
                        current,
                        current.MarkRuntimeCellReady(now),
                        transaction.Session,
                        ct));

                default:
                    return transaction.Commit(0L);
            }
        }, cancellationToken);

    private async Task<long> PersistRecoveryAsync(
        ResourceDeployment current,
        ResourceDeployment recovered,
        IClientSessionHandle session,
        CancellationToken cancellationToken,
        long staleClaimRecoveryCount = 0)
    {
        var replacement = await resourceDeployments.ReplaceOneAsync(
            session,
            ResourceDeploymentFilters.ForRecoveryAtRevision(current),
            recovered,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
            return 0L;

        await storeOutboxWrites(
            session,
            [recovered.ToCurrentRequestWrite()],
            cancellationToken);
        return staleClaimRecoveryCount;
    }
}
