// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Reconciliation;

public delegate Task<long> WakeResourceDeploymentsForRuntimeCell(
    string tenantId,
    string runtimeCellId,
    int batchSize,
    DateTime readyAtUtc,
    CancellationToken cancellationToken = default);

internal sealed class ResourceDeploymentWakePersistence(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    RunMongoOutcomeTransaction<long> runMongoTransaction,
    StoreOutboxWrites storeOutboxWrites
)
{
    public async Task<long> WakeAsync(
        string tenantId,
        string runtimeCellId,
        int batchSize,
        DateTime readyAtUtc,
        CancellationToken cancellationToken = default)
    {
        var waiting = await resourceDeployments
            .Find(ResourceDeploymentFilters.WaitingForRuntimeCell(tenantId, runtimeCellId))
            .SortBy(deployment => deployment.Id)
            .Limit(batchSize)
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
            return 0L;

        return await runMongoTransaction(
            async (transaction, ct) =>
            {
                var writes = new List<OutboxWrite>(waiting.Count);
                foreach (var deployment in waiting)
                {
                    var current = deployment.MarkRuntimeCellReady(readyAtUtc);
                    var replacement = await resourceDeployments.ReplaceOneAsync(
                        transaction.Session,
                        ResourceDeploymentFilters.WaitingForRuntimeCellAtRevision(deployment),
                        current,
                        cancellationToken: ct);
                    if (replacement.ModifiedCount == 1)
                        writes.Add(current.ToCurrentRequestWrite());
                }

                await storeOutboxWrites(transaction.Session, writes, ct);
                return transaction.Commit(writes.Count);
            },
            cancellationToken);
    }
}
