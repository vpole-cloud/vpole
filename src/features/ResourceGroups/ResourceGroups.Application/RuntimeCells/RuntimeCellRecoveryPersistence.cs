using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.RuntimeCells;

public sealed class RuntimeCellRecoveryPersistence(
    IMongoCollection<IsolationScope> isolationScopes,
    IMongoCollection<RuntimeCell> runtimeCells,
    StoreOutboxWrites storeOutboxWrites,
    RunMongoOutcomeTransaction<Unit> runMongoTransaction)
{
    public async Task<IReadOnlyList<IsolationScope>> LoadDeletingScopesAsync(
        int batchSize,
        CancellationToken cancellationToken = default) =>
        await isolationScopes
            .Find(IsolationScopeFilters.Deleting())
            .SortBy(scope => scope.UpdatedAtUtc)
            .Limit(batchSize)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RuntimeCell>> LoadRecoveryCandidatesAsync(
        DateTime staleBefore,
        DateTime now,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        await runtimeCells
            .Find(RuntimeCellFilters.RecoveryCandidates(staleBefore, now))
            .SortBy(cell => cell.UpdatedAtUtc)
            .Limit(batchSize)
            .ToListAsync(cancellationToken);

    public Task RecoverAsync(
        string runtimeCellId,
        DateTime now,
        CancellationToken cancellationToken = default) =>
        runMongoTransaction(async (transaction, ct) =>
        {
            var current = await runtimeCells
                .Find(transaction.Session, RuntimeCellFilters.ById(runtimeCellId))
                .FirstOrDefaultAsync(ct);
            if (current is null)
                return transaction.Commit(Unit.Value);

            switch (current.State)
            {
                case ReadyRuntimeCellState when
                    current.UpdatedAtUtc <= RuntimeCellVerificationSchedule.DueBefore(now):
                    var verification = current.RequestReconciliation(now);
                    var replacement = await runtimeCells.ReplaceOneAsync(
                        transaction.Session,
                        RuntimeCellFilters.AtDesiredGenerationAndState<ReadyRuntimeCellState>(
                            current, current.DesiredGeneration),
                        verification,
                        cancellationToken: ct);
                    if (replacement.ModifiedCount == 1)
                    {
                        await storeOutboxWrites(
                            transaction.Session,
                            [OutboxWrite.Create(
                                verification.ToReconciliationRequestedEvent(),
                                RuntimeCellPendingKey.Reconciliation(
                                    verification.Id,
                                    verification.DesiredGeneration))],
                            ct);
                    }
                    break;

                case ProvisioningRuntimeCellState:
                    await storeOutboxWrites(
                        transaction.Session,
                        [OutboxWrite.Create(
                            current.ToReconciliationRequestedEvent(),
                            RuntimeCellPendingKey.Reconciliation(
                                current.Id,
                                current.DesiredGeneration))],
                        ct);
                    break;

                case DeletingRuntimeCellState deleting when deleting.NextCheckAtUtc is null || deleting.NextCheckAtUtc <= now:
                    await storeOutboxWrites(
                        transaction.Session,
                        [OutboxWrite.Create(
                            current.ToDeletionRequestedEvent(),
                            RuntimeCellPendingKey.DeletionCheck(
                                current.Id,
                                current.DesiredGeneration,
                                deleting.CheckSequence))],
                        ct);
                    break;

                case FailedRuntimeCellState { RetryNotBeforeUtc: var retryAt }
                    when retryAt <= now:
                    await RetryFailedAsync(current, now, transaction, ct);
                    break;
            }

            return transaction.Commit(Unit.Value);
        }, cancellationToken);

    private async Task RetryFailedAsync(
        RuntimeCell current,
        DateTime now,
        MongoTransactionContext<Unit> transaction,
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
        var replacement = await runtimeCells.ReplaceOneAsync(
            transaction.Session,
            RuntimeCellFilters.AtDesiredGenerationFailed(current),
            retried,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount == 0)
            return;

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
        await storeOutboxWrites(transaction.Session, [write], cancellationToken);
    }
}
