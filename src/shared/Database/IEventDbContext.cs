using System.Reflection;
using System.Text.Json;
using MassTransit;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;
using EventExtensions = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Database;

public interface IEventDbContext : IMongoDbContext
{
    IMongoCollection<OutboxMessage> OutboxMessages { get; }
}

public static class IMongoDbContextExtensions
{
    public static async Task StoreEventAsync<TEvent>(this IEventDbContext dbContext, TEvent @event, IClientSessionHandle session, CancellationToken cancellationToken = default)
        where TEvent : IPoleEvent
    {
        var eventType = typeof(TEvent);
        var entityNameAttribute = eventType.GetCustomAttribute<EntityNameAttribute>();

        var eventTypeName = entityNameAttribute?.EntityName ?? eventType.FullName ?? eventType.Name;

        var payloadJson = JsonSerializer.Serialize(@event, EventExtensions.SerializerOptions);

        var outboxMessage = new OutboxMessage
        {
            EventType = eventTypeName,
            PayloadJson = payloadJson,
            OccurredOn = DateTime.UtcNow,
        };

        await dbContext.OutboxMessages.InsertOneAsync(session, outboxMessage, cancellationToken: cancellationToken);
    }
}