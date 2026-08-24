using MongoDB.Driver;
using Pole.Features.Shared.Models;

namespace Pole.Features.Outbox.Application.Persistence;

public sealed record OutboxCleanupRequest(
    DateTime PublishedCutoffUtc,
    DateTime AbandonedCutoffUtc,
    int BatchSize);

public sealed record OutboxCleanupResult(
    long PublishedCount,
    long AbandonedCount);

public delegate Task<OutboxCleanupResult> CleanupTerminalOutboxMessages(
    OutboxCleanupRequest request,
    CancellationToken cancellationToken = default);

internal sealed class MongoCleanupTerminalOutboxMessages(
    IMongoCollection<OutboxMessage> messages)
{
    public async Task<OutboxCleanupResult> InvokeAsync(
        OutboxCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.BatchSize, 1);

        var publishedCount = await DeletePublishedBatchesAsync(
            request.PublishedCutoffUtc,
            request.BatchSize,
            cancellationToken);
        var abandonedCount = await DeleteAbandonedBatchesAsync(
            request.AbandonedCutoffUtc,
            request.BatchSize,
            cancellationToken);
        return new OutboxCleanupResult(publishedCount, abandonedCount);
    }

    private async Task<long> DeletePublishedBatchesAsync(
        DateTime cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        long totalDeleted = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await DeletePublishedBatchAsync(
                cutoffUtc,
                batchSize,
                cancellationToken);
            totalDeleted += deleted;
            if (deleted < batchSize)
                return totalDeleted;
        }
    }

    private async Task<long> DeletePublishedBatchAsync(
        DateTime cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var ids = await messages
            .Find(OutboxMessageFilters.ExpiredPublished(cutoffUtc))
            .SortBy(message =>
                ((PublishedOutboxMessageState)message.State).PublishedAtUtc)
            .ThenBy(message => message.Id)
            .Limit(batchSize)
            .Project(message => message.Id)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
            return 0;

        var result = await messages.DeleteManyAsync(
            OutboxMessageFilters.PublishedWithIds(ids),
            cancellationToken);
        return result.DeletedCount;
    }

    private async Task<long> DeleteAbandonedBatchesAsync(
        DateTime cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        long totalDeleted = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await DeleteAbandonedBatchAsync(
                cutoffUtc,
                batchSize,
                cancellationToken);
            totalDeleted += deleted;
            if (deleted < batchSize)
                return totalDeleted;
        }
    }

    private async Task<long> DeleteAbandonedBatchAsync(
        DateTime cutoffUtc,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var ids = await messages
            .Find(OutboxMessageFilters.ExpiredAbandoned(cutoffUtc))
            .SortBy(message =>
                ((AbandonedOutboxMessageState)message.State).AbandonedAtUtc)
            .ThenBy(message => message.Id)
            .Limit(batchSize)
            .Project(message => message.Id)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
            return 0;

        var result = await messages.DeleteManyAsync(
            OutboxMessageFilters.AbandonedWithIds(ids),
            cancellationToken);
        return result.DeletedCount;
    }
}
