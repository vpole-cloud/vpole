using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Observability;
using EventExtensions = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Features.Outbox.Application.Persistence;

internal sealed class MongoStoreOutboxWrites(IMongoDatabase database)
{
    private readonly IMongoCollection<OutboxMessage> _outboxMessages =
        database.GetCollection<OutboxMessage>("OutboxMessages");

    public Task InvokeAsync(
        IClientSessionHandle session,
        IReadOnlyCollection<OutboxWrite> writes,
        CancellationToken cancellationToken = default) =>
        MongoOutboxPersistence.StoreAsync(
            _outboxMessages,
            session,
            writes,
            cancellationToken);
}

internal static class MongoOutboxPersistence
{
    public static async Task StoreAsync(
        IMongoCollection<OutboxMessage> outboxMessages,
        IClientSessionHandle session,
        IReadOnlyCollection<OutboxWrite> writes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outboxMessages);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(writes);

        var messages = writes
            .Select(CreateOutboxMessage)
            .ToArray();
        if (messages.Length == 0)
            return;

        if (messages.Length == 1 && messages[0].PendingKey is null)
        {
            await outboxMessages.InsertOneAsync(
                session,
                messages[0],
                cancellationToken: cancellationToken);
            return;
        }

        if (messages.All(message => message.PendingKey is null))
        {
            await outboxMessages.InsertManyAsync(
                session,
                messages,
                new InsertManyOptions { IsOrdered = true },
                cancellationToken);
            return;
        }

        var writeModels = messages
            .Select(CreateWriteModel)
            .ToArray();
        await outboxMessages.BulkWriteAsync(
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
            OutboxMessageFilters.ByPendingKey(message.PendingKey),
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
