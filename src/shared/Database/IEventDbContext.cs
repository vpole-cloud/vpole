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
        var eventType = typeof(TEvent);
        var entityNameAttribute = eventType.GetCustomAttribute<EntityNameAttribute>();

        var eventTypeName = entityNameAttribute?.EntityName ?? eventType.FullName ?? eventType.Name;

        var payloadJson = JsonSerializer.Serialize(@event, EventExtensions.SerializerOptions);
        var traceContext = TraceContext.Create(@event.TraceId, @event.CorrelationId);

        var outboxMessage = new OutboxMessage
        {
            EventType = eventTypeName,
            PayloadJson = payloadJson,
            TraceId = traceContext.TraceId,
            CorrelationId = traceContext.CorrelationId,
            OccurredOn = DateTime.UtcNow,
        };

        await dbContext.OutboxMessages.InsertOneAsync(session, outboxMessage, cancellationToken: cancellationToken);
    }
}
