using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;
using Pole.Observability;
using EventExtensions = Pole.Features.Events.Abstractions.EventExtensions;

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
        StoreWritesAsync(dbContext, [write], session, cancellationToken);

    /// <summary>
    /// Stores independent outbox messages in one ordered Mongo write.
    /// Keyed writes use an atomic pending-key upsert and existing keys are
    /// successful no-ops.
    /// </summary>
    public static Task StoreEventAsync(
        this IEventDbContext dbContext,
        IEnumerable<OutboxWrite> writes,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        StoreWritesAsync(dbContext, writes, session, cancellationToken);

    private static async Task StoreWritesAsync(
        IEventDbContext dbContext,
        IEnumerable<OutboxWrite> writes,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var messages = writes
            .Select(CreateOutboxMessage)
            .ToArray();
        if (messages.Length == 0)
            return;

        if (messages.Length == 1 && messages[0].PendingKey is null)
        {
            await dbContext.OutboxMessages.InsertOneAsync(
                session,
                messages[0],
                cancellationToken: cancellationToken);
            return;
        }

        if (messages.All(message => message.PendingKey is null))
        {
            await dbContext.OutboxMessages.InsertManyAsync(
                session,
                messages,
                new InsertManyOptions { IsOrdered = true },
                cancellationToken);
            return;
        }

        var writeModels = messages
            .Select(CreateWriteModel)
            .ToArray();
        await dbContext.OutboxMessages.BulkWriteAsync(
            session,
            writeModels,
            new BulkWriteOptions { IsOrdered = true },
            cancellationToken);
    }

    private static WriteModel<OutboxMessage> CreateWriteModel(
        OutboxMessage message)
    {
        if (message.PendingKey is null)
            return new InsertOneModel<OutboxMessage>(message);

        var update = Builders<OutboxMessage>.Update
            .SetOnInsert(item => item.Id, message.Id)
            .SetOnInsert(item => item.EventType, message.EventType)
            .SetOnInsert(item => item.TenantId, message.TenantId)
            .SetOnInsert(item => item.PayloadJson, message.PayloadJson)
            .SetOnInsert(item => item.TraceId, message.TraceId)
            .SetOnInsert(item => item.CorrelationId, message.CorrelationId)
            .SetOnInsert(item => item.OccurredAtUtc, message.OccurredAtUtc)
            .SetOnInsert(item => item.EnqueuedAtUtc, message.EnqueuedAtUtc)
            .SetOnInsert(item => item.PendingKey, message.PendingKey)
            .SetOnInsert(item => item.State, message.State);

        return new UpdateOneModel<OutboxMessage>(
            Builders<OutboxMessage>.Filter.Eq(
                item => item.PendingKey,
                message.PendingKey),
            update)
        {
            IsUpsert = true
        };
    }

    private static OutboxMessage CreateOutboxMessage(OutboxWrite write)
    {
        var @event = write.Event;
        var eventType = @event.GetType();
        var eventTypeName = EventTypeIdentity.GetRequiredName(eventType);
        var payloadJson = JsonSerializer.Serialize(
            @event,
            eventType,
            EventExtensions.SerializerOptions);
        var traceContext = TraceContext.Create(
            @event.EventCore.TraceId,
            @event.EventCore.CorrelationId);

        return OutboxMessage.Create(
            eventTypeName,
            @event.EventCore.TenantId,
            payloadJson,
            traceContext.TraceId,
            traceContext.CorrelationId,
            @event.EventCore.OccurredOn,
            DateTime.UtcNow,
            write.PendingKey);
    }
}
