using MongoDB.Driver;
using Pole.Features.Shared.Models;

namespace Pole.Features.Outbox.Application.Persistence;

public delegate Task<IReadOnlyList<OutboxMessage>> LoadDueOutboxMessages(
    DateTime dueAtUtc,
    int batchSize,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadDueOutboxMessages(
    IMongoCollection<OutboxMessage> messages)
{
    public async Task<IReadOnlyList<OutboxMessage>> InvokeAsync(
        DateTime dueAtUtc,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        return await messages
            .Find(OutboxMessageFilters.PendingDue(dueAtUtc))
            .Sort(Builders<OutboxMessage>.Sort
                .Ascending(message =>
                    ((PendingOutboxMessageState)message.State).NextAttemptAtUtc)
                .Ascending(message => message.EnqueuedAtUtc)
                .Ascending(message => message.Id))
            .Limit(batchSize)
            .ToListAsync(cancellationToken);
    }
}
