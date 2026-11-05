using Pole.Features.Outbox.Application.Mongo;
using Pole.Features.Outbox.Application;
using MongoDB.Driver;

namespace Pole.Features.Outbox.Application.Delivery;

public sealed record OutboxQueueSnapshot(
    long PendingCount,
    long AbandonedCount,
    DateTime? OldestPendingAtUtc);

public delegate Task<OutboxQueueSnapshot> LoadOutboxQueueSnapshot(
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadOutboxQueueSnapshot(
    IMongoCollection<OutboxMessage> messages)
{
    public async Task<OutboxQueueSnapshot> InvokeAsync(
        CancellationToken cancellationToken = default)
    {
        var pendingCount = await messages.CountDocumentsAsync(
            OutboxMessageFilters.Pending(),
            cancellationToken: cancellationToken);
        var abandonedCount = await messages.CountDocumentsAsync(
            OutboxMessageFilters.Abandoned(),
            cancellationToken: cancellationToken);
        var oldestPendingAtUtc = await messages
            .Find(OutboxMessageFilters.Pending())
            .SortBy(message => message.EnqueuedAtUtc)
            .Project(message => (DateTime?)message.EnqueuedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return new OutboxQueueSnapshot(
            pendingCount,
            abandonedCount,
            oldestPendingAtUtc);
    }
}
