using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Domain;
using Pole.Features.Outbox.Contracts;

namespace Pole.Features.Backups.Application.Reconciliation;

public sealed class BackupRecoveryScanner(
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupOperation> operations,
    MongoRunTransaction transactions,
    StoreOutboxWrites outbox,
    TimeProvider clock
)
{
    internal const int BatchSize = 100;

    public Task RequestDueWorkAsync(CancellationToken ct) => transactions.InvokeOutcomeAsync<bool>(
        async (tx, token) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var duePolicies = await policies.Find(tx.Session, BackupDispatchFilters.PoliciesDue(now))
                .SortBy(policy => policy.Dispatch.NextAttemptAtUtc)
                .Limit(BatchSize)
                .ToListAsync(token);
            var dueOperations = await operations.Find(tx.Session, BackupDispatchFilters.OperationsDue(now))
                .SortBy(operation => operation.CreatedAt)
                .Limit(BatchSize)
                .ToListAsync(token);
            var requests = duePolicies.Select(BackupWorkRequests.Policy)
                .Concat(dueOperations.Select(BackupWorkRequests.Operation))
                .ToArray();
            if (requests.Length == 0)
                return tx.Abort(false);
            await outbox(tx.Session, requests, token);
            return tx.Commit(true);
        },
        ct);
}
