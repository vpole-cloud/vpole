using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

public delegate Task<IReadOnlyList<string>> LoadResourceDeploymentRecoveryCandidates(
    DateTime now,
    DateTime stalePendingBefore,
    int batchSize,
    CancellationToken cancellationToken = default);

public delegate Task<Result<long>> RecoverResourceDeployment(
    string deploymentId,
    DateTime now,
    CancellationToken cancellationToken = default);

internal sealed class ResourceDeploymentRecoveryPersistence(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    RunMongoTransaction<long> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites,
    IResourcePlacementLookup placementLookup)
{
    public async Task<IReadOnlyList<string>> LoadCandidatesAsync(
        DateTime now,
        DateTime stalePendingBefore,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        await resourceDeployments
            .Find(Builders<ResourceDeployment>.Filter.Or(
                Builders<ResourceDeployment>.Filter.And(
                    Builders<ResourceDeployment>.Filter
                        .OfType<ResourceDeploymentState, PendingResourceDeploymentState>(
                            deployment => deployment.State),
                    Builders<ResourceDeployment>.Filter.Lte(
                        deployment => deployment.UpdatedAtUtc,
                        stalePendingBefore)),
                Builders<ResourceDeployment>.Filter.And(
                    Builders<ResourceDeployment>.Filter
                        .OfType<ResourceDeploymentState, ReconcilingResourceDeploymentState>(
                            deployment => deployment.State),
                    Builders<ResourceDeployment>.Filter.Lte(
                        deployment =>
                            ((ReconcilingResourceDeploymentState)deployment.State).LeaseExpiresAtUtc,
                        now)),
                Builders<ResourceDeployment>.Filter.And(
                    Builders<ResourceDeployment>.Filter
                        .OfType<ResourceDeploymentState, FailedResourceDeploymentState>(
                            deployment => deployment.State),
                    Builders<ResourceDeployment>.Filter.Eq(
                        deployment =>
                            ((FailedResourceDeploymentState)deployment.State).Retryable,
                        true),
                    Builders<ResourceDeployment>.Filter.Lte(
                        deployment =>
                            ((FailedResourceDeploymentState)deployment.State).RetryNotBeforeUtc,
                        now)),
                Builders<ResourceDeployment>.Filter.And(
                    Builders<ResourceDeployment>.Filter
                        .OfType<ResourceDeploymentState, WaitingForRuntimeCellResourceDeploymentState>(
                            deployment => deployment.State),
                    Builders<ResourceDeployment>.Filter.Lte(
                        deployment => deployment.UpdatedAtUtc,
                        stalePendingBefore))))
            .SortBy(deployment => deployment.UpdatedAtUtc)
            .ThenBy(deployment => deployment.Id)
            .Limit(batchSize)
            .Project(deployment => deployment.Id)
            .ToListAsync(cancellationToken);

    public Task<Result<long>> RecoverAsync(
        string deploymentId,
        DateTime now,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (session, ct) =>
        {
            var current = await resourceDeployments
                .Find(session, deployment => deployment.Id == deploymentId)
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
                    var runtimeCell = await placementLookup.FindAsync(
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
            Builders<ResourceDeployment>.Filter.And(
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.Id,
                    current.Id),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.TenantId,
                    current.TenantId),
                Builders<ResourceDeployment>.Filter.Eq(
                    deployment => deployment.Revision,
                    current.Revision),
                CurrentStateFilter(current.State)),
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

    private static FilterDefinition<ResourceDeployment> CurrentStateFilter(
        ResourceDeploymentState state) =>
        state switch
        {
            WaitingForRuntimeCellResourceDeploymentState =>
                Builders<ResourceDeployment>.Filter
                    .OfType<ResourceDeploymentState, WaitingForRuntimeCellResourceDeploymentState>(
                        deployment => deployment.State),
            PendingResourceDeploymentState =>
                Builders<ResourceDeployment>.Filter
                    .OfType<ResourceDeploymentState, PendingResourceDeploymentState>(
                        deployment => deployment.State),
            ReconcilingResourceDeploymentState reconciling =>
                Builders<ResourceDeployment>.Filter.And(
                    Builders<ResourceDeployment>.Filter
                        .OfType<ResourceDeploymentState, ReconcilingResourceDeploymentState>(
                            deployment => deployment.State),
                    Builders<ResourceDeployment>.Filter.Eq(
                        deployment =>
                            ((ReconcilingResourceDeploymentState)deployment.State).ClaimId,
                        reconciling.ClaimId)),
            FailedResourceDeploymentState =>
                Builders<ResourceDeployment>.Filter
                    .OfType<ResourceDeploymentState, FailedResourceDeploymentState>(
                        deployment => deployment.State),
            _ => throw new InvalidOperationException(
                $"State '{state.GetType().Name}' cannot be recovered.")
        };
}
