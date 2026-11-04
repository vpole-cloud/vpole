using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence.RuntimeCells;

public union RetryRuntimeCellResult(
    RuntimeCell,
    RuntimeCellNotFound,
    RuntimeCellRetryRejected,
    ResourceGroupRevisionConflict);

public delegate Task<RetryRuntimeCellResult> PersistRuntimeCellRetry(
    string tenantId,
    string resourceGroupId,
    string runtimeCellId,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistRuntimeCellRetry(
    IMongoCollection<RuntimeCell> runtimeCells,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<RetryRuntimeCellResult> runMongoTransaction,
    TimeProvider timeProvider)
{
    public Task<RetryRuntimeCellResult> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        string runtimeCellId,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (transaction, ct) =>
        {
            var current = await runtimeCells
                .Find(
                    transaction.Session,
                    RuntimeCellFilters.ByTenantResourceGroupAndId(
                        tenantId,
                        resourceGroupId,
                        runtimeCellId))
                .FirstOrDefaultAsync(ct);
            if (current is null)
            {
                return transaction.Abort(new RetryRuntimeCellResult(
                    new RuntimeCellNotFound(runtimeCellId)));
            }
            if (current.State is not FailedRuntimeCellState failed)
            {
                return transaction.Abort(new RetryRuntimeCellResult(
                    new RuntimeCellRetryRejected(runtimeCellId)));
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var retried = failed.Operation switch
            {
                RuntimeCellOperation.Reconciliation => current.RequestReconciliation(now),
                RuntimeCellOperation.Deletion => current.RequestDeletion(now),
                _ => throw new InvalidOperationException(
                    $"Runtime cell '{current.Id}' has an unsupported failed operation.")
            };
            var replacement = await runtimeCells.ReplaceOneAsync(
                transaction.Session,
                RuntimeCellFilters.AtDesiredGenerationAndState<FailedRuntimeCellState>(
                    current,
                    current.DesiredGeneration),
                retried,
                cancellationToken: ct);
            if (replacement.ModifiedCount != 1)
            {
                return transaction.Abort(new RetryRuntimeCellResult(
                    new ResourceGroupRevisionConflict(
                        resourceGroupId,
                        ResourceGroupRevisionConflictReason.ExpectedRevisionConflict)));
            }

            var write = failed.Operation == RuntimeCellOperation.Reconciliation
                ? OutboxWrite.Create(
                    retried.ToReconciliationRequestedEvent(),
                    RuntimeCellPendingKey.Reconciliation(
                        retried.Id,
                        retried.DesiredGeneration))
                : OutboxWrite.Create(
                    retried.ToDeletionRequestedEvent(),
                    RuntimeCellPendingKey.Deletion(
                        retried.Id,
                        retried.DesiredGeneration));
            await storeOutboxWrites(transaction.Session, [write], ct);
            return transaction.Commit(new RetryRuntimeCellResult(retried));
        }, cancellationToken);
}
