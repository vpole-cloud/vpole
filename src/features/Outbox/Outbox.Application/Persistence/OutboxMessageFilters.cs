using MongoDB.Driver;
namespace Pole.Features.Outbox.Application.Persistence;

internal static class OutboxMessageFilters
{
    public static FilterDefinition<OutboxMessage> ByPendingKey(
        string pendingKey) =>
        Builders<OutboxMessage>.Filter.Eq(
            message => message.PendingKey,
            pendingKey);

    public static FilterDefinition<OutboxMessage> Pending() =>
        Builders<OutboxMessage>.Filter
            .OfType<OutboxMessageState, PendingOutboxMessageState>(
                message => message.State);

    public static FilterDefinition<OutboxMessage> Abandoned() =>
        Builders<OutboxMessage>.Filter
            .OfType<OutboxMessageState, AbandonedOutboxMessageState>(
                message => message.State);

    public static FilterDefinition<OutboxMessage> Published() =>
        Builders<OutboxMessage>.Filter
            .OfType<OutboxMessageState, PublishedOutboxMessageState>(
                message => message.State);

    public static FilterDefinition<OutboxMessage> PendingDue(DateTime dueAtUtc) =>
        Builders<OutboxMessage>.Filter.And(
            Pending(),
            Builders<OutboxMessage>.Filter.Lte(
                message => ((PendingOutboxMessageState)message.State).NextAttemptAtUtc,
                dueAtUtc));

    public static FilterDefinition<OutboxMessage> PendingAtAttempt(
        OutboxMessage expected) =>
        Builders<OutboxMessage>.Filter.And(
            Builders<OutboxMessage>.Filter.Eq(message => message.Id, expected.Id),
            Pending(),
            Builders<OutboxMessage>.Filter.Eq(
                message => ((PendingOutboxMessageState)message.State).AttemptCount,
                ((PendingOutboxMessageState)expected.State).AttemptCount));

    public static FilterDefinition<OutboxMessage> ExpiredPublished(
        DateTime cutoffUtc) =>
        Builders<OutboxMessage>.Filter.And(
            Published(),
            Builders<OutboxMessage>.Filter.Lt(
                message => ((PublishedOutboxMessageState)message.State).PublishedAtUtc,
                cutoffUtc));

    public static FilterDefinition<OutboxMessage> ExpiredAbandoned(
        DateTime cutoffUtc) =>
        Builders<OutboxMessage>.Filter.And(
            Abandoned(),
            Builders<OutboxMessage>.Filter.Lt(
                message => ((AbandonedOutboxMessageState)message.State).AbandonedAtUtc,
                cutoffUtc));

    public static FilterDefinition<OutboxMessage> PublishedWithIds(
        IReadOnlyCollection<string> ids) =>
        Builders<OutboxMessage>.Filter.And(
            Published(),
            Builders<OutboxMessage>.Filter.In(message => message.Id, ids));

    public static FilterDefinition<OutboxMessage> AbandonedWithIds(
        IReadOnlyCollection<string> ids) =>
        Builders<OutboxMessage>.Filter.And(
            Abandoned(),
            Builders<OutboxMessage>.Filter.In(message => message.Id, ids));
}
