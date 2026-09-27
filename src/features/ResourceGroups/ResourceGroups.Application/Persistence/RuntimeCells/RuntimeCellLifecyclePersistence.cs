using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Kubernetes;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Persistence.RuntimeCells;

public sealed class RuntimeCellLifecyclePersistence(
    IMongoCollection<RuntimeCell> runtimeCells,
    StoreOutboxWrites storeOutboxWrites,
    ApplyDeploymentTargetReferenceChanges applyDeploymentTargetReferenceChanges,
    RunMongoOutcomeTransaction<Unit> runUnitTransaction,
    RunMongoOutcomeTransaction<bool> runBooleanTransaction,
    TimeProvider timeProvider)
{
    public async Task<RuntimeCell?> LoadAsync(
        string tenantId,
        string runtimeCellId,
        CancellationToken cancellationToken = default) =>
        await runtimeCells
            .Find(RuntimeCellFilters.ByTenantAndId(tenantId, runtimeCellId))
            .FirstOrDefaultAsync(cancellationToken);

    public Task PersistReconciliationAsync(
        RuntimeCell runtimeCell,
        long generation,
        RuntimeCellNamespaceOperationOutcome namespaceOutcome,
        CancellationToken cancellationToken = default) =>
        runUnitTransaction(async (transaction, ct) =>
        {
            if (namespaceOutcome is Unit)
            {
                var ready = runtimeCell.MarkReady(
                    generation,
                    timeProvider.GetUtcNow().UtcDateTime);
                var replacement = await runtimeCells.ReplaceOneAsync(
                    transaction.Session,
                    RuntimeCellFilters.AtDesiredGenerationAndState<ProvisioningRuntimeCellState>(
                        runtimeCell,
                        generation),
                    ready,
                    cancellationToken: ct);
                if (replacement.ModifiedCount == 1)
                {
                    await storeOutboxWrites(
                        transaction.Session,
                        [OutboxWrite.Create(ready.ToReadyEvent())],
                        ct);
                }

                return transaction.Commit(Unit.Value);
            }

            var attemptedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            var failed = runtimeCell.MarkFailed(
                generation,
                RuntimeCellOperation.Reconciliation,
                FailureCode(namespaceOutcome),
                "The Kubernetes namespace reconciliation attempt failed.",
                attemptedAtUtc,
                attemptedAtUtc.Add(RetryDelay(generation)));
            var failureWrite = await runtimeCells.ReplaceOneAsync(
                transaction.Session,
                RuntimeCellFilters.AtDesiredGenerationAndState<ProvisioningRuntimeCellState>(
                    runtimeCell,
                    generation),
                failed,
                cancellationToken: ct);
            if (failureWrite.ModifiedCount == 1)
            {
                await storeOutboxWrites(
                    transaction.Session,
                    [OutboxWrite.Create(failed.ToReconciliationFailedEvent())],
                    ct);
            }

            return transaction.Commit(Unit.Value);
        }, cancellationToken);

    public Task<bool> PersistDeletionAsync(
        RuntimeCell runtimeCell,
        long generation,
        RuntimeCellNamespaceOperationOutcome namespaceOutcome,
        CancellationToken cancellationToken = default) =>
        runBooleanTransaction(async (transaction, ct) =>
        {
            if (namespaceOutcome is not Unit)
            {
                var attemptedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
                var failed = runtimeCell.MarkFailed(
                    generation,
                    RuntimeCellOperation.Deletion,
                    FailureCode(namespaceOutcome),
                    "The Kubernetes namespace deletion attempt failed.",
                    attemptedAtUtc,
                    attemptedAtUtc.Add(RetryDelay(generation)));
                await runtimeCells.ReplaceOneAsync(
                    transaction.Session,
                    RuntimeCellFilters.AtDesiredGenerationAndState<DeletingRuntimeCellState>(
                        runtimeCell,
                        generation),
                    failed,
                    cancellationToken: ct);
                return transaction.Commit(false);
            }

            var deleted = runtimeCell.MarkDeleted(
                generation,
                timeProvider.GetUtcNow().UtcDateTime);
            var replacement = await runtimeCells.ReplaceOneAsync(
                transaction.Session,
                RuntimeCellFilters.AtDesiredGenerationAndState<DeletingRuntimeCellState>(
                    runtimeCell,
                    generation),
                deleted,
                cancellationToken: ct);
            if (replacement.ModifiedCount == 0)
                return transaction.Commit(true);

            var unregister = await applyDeploymentTargetReferenceChanges(
                new DeploymentTargetReferenceOwner(
                    ResourcePlacementReferenceOwnerTypes.RuntimeCell,
                    runtimeCell.Id),
                [],
                [runtimeCell.DeploymentTargetId],
                transaction.Session,
                ct);
            if (unregister is DeploymentTargetReferenceChangeRejected)
                return transaction.Abort(false);

            await storeOutboxWrites(
                transaction.Session,
                [OutboxWrite.Create(deleted.ToDeletedEvent())],
                ct);
            return transaction.Commit(true);
        }, cancellationToken);

    public static string FailureCode(RuntimeCellNamespaceOperationOutcome outcome) =>
        outcome switch
        {
            Unit => throw new ArgumentException(
                "A successful namespace operation has no failure code.",
                nameof(outcome)),
            RuntimeCellNamespaceOwnershipConflict => "NamespaceOwnershipConflict",
            RuntimeCellNamespaceUnavailable { DeletionPending: true } =>
                "NamespaceDeletionPending",
            RuntimeCellNamespaceUnavailable => "NamespaceOperationFailed"
        };

    public static TimeSpan RetryDelay(long generation)
    {
        var exponent = (int)Math.Clamp(generation - 1, 0, 5);
        return TimeSpan.FromSeconds(Math.Min(30 * (1 << exponent), 900));
    }
}
