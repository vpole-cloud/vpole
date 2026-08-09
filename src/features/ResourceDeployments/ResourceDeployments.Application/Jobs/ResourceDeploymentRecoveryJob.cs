using Hangfire;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Jobs;

public sealed class ResourceDeploymentRecoveryJob(
    IResourceDeploymentDbContext dbContext,
    IResourcePlacementLookup placementLookup,
    ResourceDeploymentMetrics metrics,
    ResourceDeploymentSettings settings,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan PendingRecoveryDelay = TimeSpan.FromSeconds(30);

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var stalePendingBefore = now.Subtract(PendingRecoveryDelay);
        var candidates = await dbContext.ResourceDeployments
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
            .Limit(settings.RecoveryBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RecoverAsync(candidate.Id, now, cancellationToken);
        }
    }

    private async Task<Result> RecoverAsync(
        string deploymentId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var recovery = await dbContext.RunInTransactionAsync<long>(
            async (session, ct) =>
            {
                var current = await dbContext.ResourceDeployments
                    .Find(session, deployment => deployment.Id == deploymentId)
                    .FirstOrDefaultAsync(ct);
                if (current is null)
                    return 0L;

                switch (current.State)
                {
                    case PendingResourceDeploymentState:
                        await dbContext.StoreEventAsync(
                            current.ToCurrentRequestWrite(),
                            session,
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
            },
            cancellationToken: cancellationToken);

        if (!recovery.TryGetValue(out var staleClaimRecoveryCount, out var failure))
            return failure;

        metrics.RecordStaleClaimRecoveries(staleClaimRecoveryCount);
        return Result.Success();
    }

    private async Task<Result<long>> PersistRecoveryAsync(
        ResourceDeployment current,
        ResourceDeployment recovered,
        IClientSessionHandle session,
        CancellationToken cancellationToken,
        long staleClaimRecoveryCount = 0)
    {
        var replacement = await dbContext.ResourceDeployments.ReplaceOneAsync(
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

        await dbContext.StoreEventAsync(
            recovered.ToCurrentRequestWrite(),
            session,
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
