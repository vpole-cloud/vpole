using System.Reflection;
using System.Text.Json;
using MassTransit;
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
    public static async Task StoreEventAsync<TEvent>(this IEventDbContext dbContext, TEvent @event, IClientSessionHandle session, CancellationToken cancellationToken = default)
        where TEvent : IPoleEvent
    {
        var outboxMessage = CreateOutboxMessage(@event, typeof(TEvent));

        await dbContext.OutboxMessages.InsertOneAsync(
            session,
            outboxMessage,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Stores multiple events in the durable outbox in one ordered insert.
    /// </summary>
    public static async Task StoreEventAsync(
        this IEventDbContext dbContext,
        IEnumerable<IPoleEvent> events,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var outboxMessages = events
            .Select(@event => CreateOutboxMessage(@event, @event.GetType()))
            .ToArray();

        if (outboxMessages.Length == 0)
            return;

        await dbContext.OutboxMessages.InsertManyAsync(
            session,
            outboxMessages,
            cancellationToken: cancellationToken);
    }

    private static OutboxMessage CreateOutboxMessage(IPoleEvent @event, Type eventType)
    {
        var entityNameAttribute = eventType.GetCustomAttribute<EntityNameAttribute>();
        var eventTypeName = entityNameAttribute?.EntityName ?? eventType.FullName ?? eventType.Name;
        var payloadJson = JsonSerializer.Serialize(@event, eventType, EventExtensions.SerializerOptions);
        var traceContext = TraceContext.Create(@event.EventCore.TraceId, @event.EventCore.CorrelationId);

        return new OutboxMessage
        {
            EventType = eventTypeName,
            TenantId = @event.EventCore.TenantId,
            PayloadJson = payloadJson,
            TraceId = traceContext.TraceId,
            CorrelationId = traceContext.CorrelationId,
            OccurredOn = DateTime.UtcNow,
        };
    }
}
