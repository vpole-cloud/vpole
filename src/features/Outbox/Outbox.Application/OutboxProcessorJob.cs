using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using Hangfire;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;
using Pole.Observability;
using EventExtensions = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Features.Outbox.Application;

/// <summary>
/// Publishes durable outbox messages and restores their trace context before handing them to MassTransit.
/// </summary>
public partial class OutboxProcessorJob(
    IEventDbContext dbContext,
    IPublishEndpoint publishEndpoint,
    OutboxEventAssemblyCatalog eventAssemblyCatalog,
    IOptions<OutboxSettings> outboxSettings,
    ILogger<OutboxProcessorJob> logger)
{
    private readonly OutboxSettings _settings = outboxSettings.Value;
    private readonly FrozenDictionary<string, Type> _eventTypeCache = BuildEventTypeCache(eventAssemblyCatalog.Assemblies);

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
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
                var poleEvent = @event as IPoleEvent;
                var carrier = @event as ITraceContextCarrier;
                var traceContext = TraceContext.Create(
                    message.TraceId ?? poleEvent?.EventCore.TraceId ?? carrier?.TraceId,
                    message.CorrelationId ?? poleEvent?.EventCore.CorrelationId ?? carrier?.CorrelationId);

                using var contextScope = TraceContext.Push(traceContext);
                using var loggerScope = logger.BeginScope(new Dictionary<string, object>
                {
                    ["TraceId"] = traceContext.TraceId,
                    ["CorrelationId"] = traceContext.CorrelationId
                });

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

    private static FrozenDictionary<string, Type> BuildEventTypeCache(IEnumerable<Assembly> assemblies)
    {
        var eventTypes = assemblies
            .SelectMany(GetLoadableTypes)
            .Where(t =>
                t is { IsClass: true, IsAbstract: false }
                && typeof(IPoleEvent).IsAssignableFrom(t));

        var cache = new Dictionary<string, Type>();

        foreach (var eventType in eventTypes)
        {
            var entityNameAttribute = eventType.GetCustomAttribute<EntityNameAttribute>();
            if (entityNameAttribute != null)
            {
                cache[entityNameAttribute.EntityName] = eventType;
            }
        }

        return cache.ToFrozenDictionary();
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
    
    private object? DeserializeEvent(string eventType, string payloadJson)
    {
        if (!_eventTypeCache.TryGetValue(eventType, out var type))
        {
            logger.LogWarning("Unknown event type: {EventType}. Available types: {AvailableTypes}", 
                eventType, string.Join(", ", _eventTypeCache.Keys));
            return null;
        }

        return JsonSerializer.Deserialize(payloadJson, type, EventExtensions.SerializerOptions);
    }

    [LoggerMessage(LogLevel.Information, "Successfully processed outbox message {messageId} of type {eventType}")]
    static partial void LogSuccessfullyProcessedOutboxMessage(ILogger<OutboxProcessorJob> logger, string messageId, string eventType);
}

