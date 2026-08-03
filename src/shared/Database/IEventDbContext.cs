using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;

namespace Pole.Database;

public interface IEventDbContext : IMongoDbContext
{
    IMongoCollection<OutboxMessage> OutboxMessages { get; }
}

public static class IMongoDbContextExtensions
{
    /// <summary>
    /// Stores an event in the durable outbox with queryable trace metadata.
    /// </summary>
    public static Task StoreEventAsync<TEvent>(
        this IEventDbContext dbContext,
        TEvent @event,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
        where TEvent : IPoleEvent =>
        dbContext.StoreEventAsync(
            OutboxWrite.Create(@event),
            session,
            cancellationToken);

    /// <summary>
    /// Stores multiple independent events as separate outbox messages.
    /// </summary>
    public static Task StoreEventAsync(
        this IEventDbContext dbContext,
        IEnumerable<IPoleEvent> events,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        dbContext.StoreEventAsync(
            events.Select(@event => OutboxWrite.Create(@event)),
            session,
            cancellationToken);

    /// <summary>
    /// Stores an event with an optional logical key that deduplicates active
    /// pending delivery requests.
    /// </summary>
    public static Task StoreEventAsync(
        this IEventDbContext dbContext,
        OutboxWrite write,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        MongoOutboxPersistence.StoreAsync(
            dbContext.OutboxMessages,
            session,
            [write],
            cancellationToken);

    /// <summary>
    /// Stores independent outbox messages in one ordered Mongo write.
    /// Keyed writes use an atomic pending-key upsert and existing keys are
    /// successful no-ops.
    /// </summary>
    public static Task StoreEventAsync(
        this IEventDbContext dbContext,
        IEnumerable<OutboxWrite> writes,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var materializedWrites = writes as IReadOnlyCollection<OutboxWrite>
            ?? writes.ToArray();
        return MongoOutboxPersistence.StoreAsync(
            dbContext.OutboxMessages,
            session,
            materializedWrites,
            cancellationToken);
    }
}
