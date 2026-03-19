using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Outbox;
using Pole.Features.Events.Abstractions;
using EventExtensions = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Features.Outbox;

public partial class OutboxProcessorJob(
    MongoDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    IOptions<OutboxSettings> outboxSettings,
    ILogger<OutboxProcessorJob> logger)
{
    private readonly OutboxSettings _settings = outboxSettings.Value;
    private static readonly ImmutableDictionary<string, Type> EventTypeCache = BuildEventTypeCache();

    public async Task ProcessOutboxMessagesAsync()
    {
        var unprocessedMessages = await dbContext.OutboxMessages
            .Find(m => m.ProcessedOn == null && m.RetryCount < _settings.MaxRetryAttempts)
            .SortBy(m => m.OccurredOn)
            .Limit(100)
            .ToListAsync();

        foreach (var message in unprocessedMessages)
        {
            try
            {
                var @event = DeserializeEvent(message.EventType, message.PayloadJson);

                if (@event == null) continue;
                await publishEndpoint.Publish(@event, @event.GetType());
                    
                var filter = Builders<OutboxMessage>.Filter.Eq(m => m.Id, message.Id);
                var update = Builders<OutboxMessage>.Update
                    .Set(m => m.ProcessedOn, DateTime.UtcNow);
                    
                await dbContext.OutboxMessages.UpdateOneAsync(filter, update);
                    
                LogSuccessfullyProcessedOutboxMessage(logger, message.Id, message.EventType);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing outbox message {MessageId} of type {EventType}", 
                    message.Id, message.EventType);
                
                var filter = Builders<OutboxMessage>.Filter.Eq(m => m.Id, message.Id);
                var update = Builders<OutboxMessage>.Update
                    .Set(m => m.RetryCount, message.RetryCount + 1)
                    .Set(m => m.LastError, ex.Message);
                
                await dbContext.OutboxMessages.UpdateOneAsync(filter, update);
            }
        }
    }

    public async Task CleanupProcessedMessagesAsync()
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-_settings.RetentionDays);
        
        var filter = Builders<OutboxMessage>.Filter.And(
            Builders<OutboxMessage>.Filter.Ne(m => m.ProcessedOn, null),
            Builders<OutboxMessage>.Filter.Lt(m => m.ProcessedOn, cutoffDate)
        );
        
        var result = await dbContext.OutboxMessages.DeleteManyAsync(filter);
        
        if (result.DeletedCount > 0)
        {
            logger.LogInformation("Cleaned up {Count} processed outbox messages older than {Days} days", 
                result.DeletedCount, _settings.RetentionDays);
        }
    }

    private static ImmutableDictionary<string, Type> BuildEventTypeCache()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var eventTypes = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(BaseEvent)));

        var cache = new Dictionary<string, Type>();

        foreach (var eventType in eventTypes)
        {
            var entityNameAttribute = eventType.GetCustomAttribute<EntityNameAttribute>();
            if (entityNameAttribute != null)
            {
                cache[entityNameAttribute.EntityName] = eventType;
            }
        }

        return cache.ToImmutableDictionary();
    }
    
    private object? DeserializeEvent(string eventType, string payloadJson)
    {
        if (!EventTypeCache.TryGetValue(eventType, out var type))
        {
            logger.LogWarning("Unknown event type: {EventType}. Available types: {AvailableTypes}", 
                eventType, string.Join(", ", EventTypeCache.Keys));
            return null;
        }

        return JsonSerializer.Deserialize(payloadJson, type, EventExtensions.SerializerOptions);
    }

    [LoggerMessage(LogLevel.Information, "Successfully processed outbox message {messageId} of type {eventType}")]
    static partial void LogSuccessfullyProcessedOutboxMessage(ILogger<OutboxProcessorJob> logger, string messageId, string eventType);
}
