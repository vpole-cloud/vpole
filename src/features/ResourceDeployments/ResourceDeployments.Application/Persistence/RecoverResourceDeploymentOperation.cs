using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

public delegate Task<Result<long>> RecoverResourceDeployment(
    string deploymentId,
    DateTime now,
    CancellationToken cancellationToken = default);

internal sealed class RecoverResourceDeploymentOperation(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    RunMongoTransaction<long> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites,
    LoadResourcePlacementInTransaction loadResourcePlacement)
{
    public Task<Result<long>> InvokeAsync(
        string deploymentId,
        DateTime now,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (session, ct) =>
        {
            var current = await resourceDeployments
                .Find(
                    session,
                    ResourceDeploymentFilters.ByDeploymentId(deploymentId))
                .FirstOrDefaultAsync(ct);
            if (current is null)
                return 0L;

            switch (current.State)
            {
                case PendingResourceDeploymentState:
                    await storeOutboxWrites(
                        session,
                        [current.ToCurrentRequestWrite()],
                        ct);
                    return 0L;

                case ReconcilingResourceDeploymentState
                    {
                        LeaseExpiresAtUtc: var leaseExpiresAtUtc
                    } when leaseExpiresAtUtc <= now:
                    return await PersistRecoveryAsync(
                        current,
                        current.RecoverStaleClaim(now),
                        session,
                        ct,
                        staleClaimRecoveryCount: 1);

                case FailedResourceDeploymentState
                    {
                        Retryable: true,
                        RetryNotBeforeUtc: var retryAtUtc
                    } when retryAtUtc <= now:
                    return await PersistRecoveryAsync(
                        current,
                        current.Retry(now),
                        session,
                        ct);

                case WaitingForRuntimeCellResourceDeploymentState:
                    var runtimeCell = await loadResourcePlacement(
                        current.TenantId,
                        current.RuntimeCellId,
                        session,
                        ct);
                    if (runtimeCell?.State != ResourcePlacementState.Ready)
                        return 0L;

                    return await PersistRecoveryAsync(
                        current,
                        current.MarkRuntimeCellReady(now),
                        session,
                        ct);

                default:
                    return 0L;
            }
        }, cancellationToken);

    private async Task<Result<long>> PersistRecoveryAsync(
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
