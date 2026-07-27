using System.Text.Json;
using Hangfire;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Services;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Shared.Models;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Jobs;

public sealed class RuntimeCellRecoveryJob(
    IResourceGroupDbContext dbContext,
    IResourceGroupDeletionFinalizer deletionFinalizer,
    TimeProvider timeProvider)
{
    private const int BatchSize = 100;
    private static readonly TimeSpan InFlightTimeout = TimeSpan.FromSeconds(30);

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var staleBefore = now.Subtract(InFlightTimeout);
        var deletingScopes = await dbContext.IsolationScopes
            .Find(Builders<IsolationScope>.Filter.Eq("Lifecycle._t", "deleting"))
            .SortBy(scope => scope.UpdatedAtUtc)
            .Limit(BatchSize)
            .ToListAsync(cancellationToken);
        foreach (var scope in deletingScopes)
        {
            await deletionFinalizer.TryFinalizeAsync(
                scope.TenantId,
                scope.ResourceGroupId,
                scope.Id,
                cancellationToken);
        }

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
        var pendingRequests = await LoadPendingRequestKeysAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (pendingRequests.Contains((candidate.Id, candidate.DesiredGeneration)))
                continue;

            await RecoverAsync(candidate.Id, now, cancellationToken);
        }
    }

    private async Task<HashSet<(string RuntimeCellId, long Generation)>>
        LoadPendingRequestKeysAsync(CancellationToken cancellationToken)
    {
        var pendingMessages = await dbContext.OutboxMessages
            .Find(Builders<OutboxMessage>.Filter.And(
                Builders<OutboxMessage>.Filter.Eq(message => message.ProcessedOn, null),
                Builders<OutboxMessage>.Filter.In(
                    message => message.EventType,
                    [
                        "runtime-cell-reconciliation-requested",
                        "runtime-cell-deletion-requested"
                    ])))
            .Project(message => message.PayloadJson)
            .ToListAsync(cancellationToken);
        var keys = new HashSet<(string RuntimeCellId, long Generation)>();
        foreach (var payloadJson in pendingMessages)
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var root = payload.RootElement;
            if (root.TryGetProperty("runtimeCellId", out var runtimeCellId) &&
                root.TryGetProperty("generation", out var generation) &&
                runtimeCellId.GetString() is { } id &&
                generation.TryGetInt64(out var value))
            {
                keys.Add((id, value));
            }
        }

        return keys;
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
