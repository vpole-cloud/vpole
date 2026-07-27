using Hangfire;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Jobs;

public sealed class RuntimeCellRecoveryJob(
    IResourceGroupDbContext dbContext,
    TimeProvider timeProvider)
{
    private const int BatchSize = 100;
    private static readonly TimeSpan InFlightTimeout = TimeSpan.FromSeconds(30);

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var staleBefore = now.Subtract(InFlightTimeout);
        var candidates = await dbContext.RuntimeCells
            .Find(Builders<RuntimeCell>.Filter.Or(
                Builders<RuntimeCell>.Filter.And(
                    Builders<RuntimeCell>.Filter.Eq("State._t", "provisioning"),
                    Builders<RuntimeCell>.Filter.Lte(cell => cell.UpdatedAtUtc, staleBefore)),
                Builders<RuntimeCell>.Filter.And(
                    Builders<RuntimeCell>.Filter.Eq("State._t", "deleting"),
                    Builders<RuntimeCell>.Filter.Lte(cell => cell.UpdatedAtUtc, staleBefore)),
                Builders<RuntimeCell>.Filter.And(
                    Builders<RuntimeCell>.Filter.Eq("State._t", "failed"),
                    Builders<RuntimeCell>.Filter.Lte("State.RetryNotBeforeUtc", now))))
            .SortBy(cell => cell.UpdatedAtUtc)
            .Limit(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
            await RecoverAsync(candidate.Id, now, cancellationToken);
    }

    private async Task RecoverAsync(
        string runtimeCellId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await dbContext.RunInTransactionAsync(async (session, ct) =>
        {
            var current = await dbContext.RuntimeCells
                .Find(session, cell => cell.Id == runtimeCellId)
                .FirstOrDefaultAsync(ct);
            if (current is null)
                return Result.Success();

            switch (current.State)
            {
                case ProvisioningRuntimeCellState:
                    await dbContext.StoreEventAsync(
                        current.ToReconciliationRequestedEvent(),
                        session,
                        ct);
                    return Result.Success();

                case DeletingRuntimeCellState:
                    await dbContext.StoreEventAsync(
                        current.ToDeletionRequestedEvent(),
                        session,
                        ct);
                    return Result.Success();

                case FailedRuntimeCellState { RetryNotBeforeUtc: var retryAt }
                    when retryAt <= now:
                    return await RetryFailedAsync(current, now, session, ct);

                default:
                    return Result.Success();
            }
        }, cancellationToken: cancellationToken);
    }

    private async Task<Result> RetryFailedAsync(
        RuntimeCell current,
        DateTime now,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var failed = (FailedRuntimeCellState)current.State;
        var retried = failed.Operation switch
        {
            RuntimeCellOperation.Reconciliation => current.RequestReconciliation(now),
            RuntimeCellOperation.Deletion => current.RequestDeletion(now),
            _ => throw new InvalidOperationException(
                $"Unsupported runtime cell operation '{failed.Operation}'.")
        };
        var replacement = await dbContext.RuntimeCells.ReplaceOneAsync(
            session,
            Builders<RuntimeCell>.Filter.And(
                Builders<RuntimeCell>.Filter.Eq(cell => cell.Id, current.Id),
                Builders<RuntimeCell>.Filter.Eq(
                    cell => cell.DesiredGeneration,
                    current.DesiredGeneration),
                Builders<RuntimeCell>.Filter.Eq("State._t", "failed")),
            retried,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount == 0)
            return Result.Success();

        if (failed.Operation == RuntimeCellOperation.Reconciliation)
        {
            await dbContext.StoreEventAsync(
                retried.ToReconciliationRequestedEvent(),
                session,
                cancellationToken);
        }
        else
        {
            await dbContext.StoreEventAsync(
                retried.ToDeletionRequestedEvent(),
                session,
                cancellationToken);
        }

        return Result.Success();
    }
}
