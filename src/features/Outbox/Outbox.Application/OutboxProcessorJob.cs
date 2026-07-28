using System.Text.Json;
using Hangfire;
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
/// Delivers durable messages with at-least-once semantics. A publish can be
/// repeated when the process stops before the pending-to-published transition.
/// </summary>
public sealed class OutboxProcessorJob(
    IEventDbContext dbContext,
    IOutboxPublisher publisher,
    OutboxEventAssemblyCatalog eventCatalog,
    IOptions<OutboxSettings> outboxSettings,
    TimeProvider timeProvider,
    OutboxRetryDelayPolicy retryDelayPolicy,
    OutboxMetrics metrics,
    ILogger<OutboxProcessorJob> logger)
{
    private const string UnknownEventTypeFailure = "UnknownEventType";
    private const string InvalidPayloadFailure = "InvalidPayload";
    private readonly OutboxSettings _settings = outboxSettings.Value;

    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task ProcessOutboxMessagesAsync(
        CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var messages = await dbContext.OutboxMessages
            .Find(PendingDueFilter(nowUtc))
            .Sort(Builders<OutboxMessage>.Sort
                .Ascending(message =>
                    ((PendingOutboxMessageState)message.State).NextAttemptAtUtc)
                .Ascending(message => message.EnqueuedAtUtc)
                .Ascending(message => message.Id))
            .Limit(_settings.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessMessageAsync(message, cancellationToken);
        }

        await UpdateQueueMetricsAsync(cancellationToken);
    }

    public async Task CleanupMessagesAsync(
        CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var publishedCutoffUtc =
            nowUtc.AddDays(-_settings.PublishedRetentionDays);
        var abandonedCutoffUtc =
            nowUtc.AddDays(-_settings.AbandonedRetentionDays);

        var publishedCount = await DeletePublishedBatchesAsync(
            publishedCutoffUtc,
            cancellationToken);
        var abandonedCount = await DeleteAbandonedBatchesAsync(
            abandonedCutoffUtc,
            cancellationToken);

        metrics.RecordCleaned("published", publishedCount);
        metrics.RecordCleaned("abandoned", abandonedCount);
        if (publishedCount + abandonedCount > 0)
        {
            logger.LogInformation(
                "Cleaned {PublishedCount} published and {AbandonedCount} abandoned outbox messages",
                publishedCount,
                abandonedCount);
        }

        await UpdateQueueMetricsAsync(cancellationToken);
    }

    private async Task ProcessMessageAsync(
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        if (!eventCatalog.EventTypes.TryGetValue(
                message.EventType,
                out var eventType))
        {
            await AbandonAsync(
                message,
                UnknownEventTypeFailure,
                $"No registered CLR event type exists for '{message.EventType}'.",
                cancellationToken);
            return;
        }

        object deserializedEvent;
        try
        {
            deserializedEvent = JsonSerializer.Deserialize(
                    message.PayloadJson,
                    eventType,
                    EventExtensions.SerializerOptions)
                ?? throw new JsonException("The event payload produced a null value.");
        }
        catch (JsonException exception)
        {
            await AbandonAsync(
                message,
                InvalidPayloadFailure,
                exception.Message,
                cancellationToken);
            return;
        }
        catch (NotSupportedException exception)
        {
            await AbandonAsync(
                message,
                InvalidPayloadFailure,
                exception.Message,
                cancellationToken);
            return;
        }

        var poleEvent = (IPoleEvent)deserializedEvent;
        if (poleEvent.EventCore is null)
        {
            await AbandonAsync(
                message,
                InvalidPayloadFailure,
                "The event payload does not contain a valid event core.",
                cancellationToken);
            return;
        }

        var traceContext = TraceContext.Create(
            message.TraceId ?? poleEvent.EventCore.TraceId,
            message.CorrelationId ?? poleEvent.EventCore.CorrelationId);
        using var contextScope = TraceContext.Push(traceContext);
        using var loggerScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["TraceId"] = traceContext.TraceId,
            ["CorrelationId"] = traceContext.CorrelationId
        });

        var startedAt = timeProvider.GetTimestamp();
        try
        {
            await publisher.PublishAsync(
                deserializedEvent,
                eventType,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ScheduleRetryAsync(
                message,
                exception,
                cancellationToken);
            return;
        }

        var publishedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var published = message.MarkPublished(publishedAtUtc);
        var replacement = await dbContext.OutboxMessages.ReplaceOneAsync(
            PendingAttemptFilter(message),
            published,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
        {
            logger.LogWarning(
                "Published outbox message {MessageId} of type {EventType}, but its pending state changed before completion could be recorded",
                message.Id,
                message.EventType);
            return;
        }

        var duration = timeProvider.GetElapsedTime(startedAt);
        metrics.RecordPublished(message.EventType, duration);
        logger.LogInformation(
            "Published outbox message {MessageId} of type {EventType} on attempt {AttemptCount}",
            message.Id,
            message.EventType,
            PendingState(message).AttemptCount + 1);
    }

    private async Task ScheduleRetryAsync(
        OutboxMessage message,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var pending = PendingState(message);
        var nextAttemptCount = checked(pending.AttemptCount + 1);
        var delay = retryDelayPolicy.Calculate(message.Id, nextAttemptCount);
        var nextAttemptAtUtc =
            timeProvider.GetUtcNow().UtcDateTime.Add(delay);
        var error = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        var retried = message.ScheduleRetry(nextAttemptAtUtc, error);

        var replacement = await dbContext.OutboxMessages.ReplaceOneAsync(
            PendingAttemptFilter(message),
            retried,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
            return;

        metrics.RecordRetry(message.EventType, delay);
        logger.LogWarning(
            exception,
            "Outbox publish failed for message {MessageId} of type {EventType} on attempt {AttemptCount}; next attempt is due at {NextAttemptAtUtc}",
            message.Id,
            message.EventType,
            nextAttemptCount,
            nextAttemptAtUtc);
    }

    private async Task AbandonAsync(
        OutboxMessage message,
        string failureCode,
        string error,
        CancellationToken cancellationToken)
    {
        var abandonedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
        var abandoned = message.Abandon(
            abandonedAtUtc,
            failureCode,
            string.IsNullOrWhiteSpace(error) ? failureCode : error);
        var replacement = await dbContext.OutboxMessages.ReplaceOneAsync(
            PendingAttemptFilter(message),
            abandoned,
            cancellationToken: cancellationToken);
        if (replacement.ModifiedCount != 1)
            return;

        metrics.RecordAbandoned(
            failureCode == UnknownEventTypeFailure
                ? "unknown"
                : message.EventType,
            failureCode);
        logger.LogError(
            "Abandoned outbox message {MessageId} of type {EventType} with failure code {FailureCode}: {Failure}",
            message.Id,
            message.EventType,
            failureCode,
            ((AbandonedOutboxMessageState)abandoned.State).LastError);
    }

    private async Task<long> DeletePublishedBatchesAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken)
    {
        long totalDeleted = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await DeletePublishedBatchAsync(
                cutoffUtc,
                cancellationToken);
            totalDeleted += deleted;
            if (deleted < _settings.CleanupBatchSize)
                return totalDeleted;
        }
    }

    private async Task<long> DeletePublishedBatchAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken)
    {
        var filter = Builders<OutboxMessage>.Filter.And(
            Builders<OutboxMessage>.Filter
                .OfType<OutboxMessageState, PublishedOutboxMessageState>(
                    message => message.State),
            Builders<OutboxMessage>.Filter.Lt(
                message => ((PublishedOutboxMessageState)message.State).PublishedAtUtc,
                cutoffUtc));
        var ids = await dbContext.OutboxMessages
            .Find(filter)
            .SortBy(message =>
                ((PublishedOutboxMessageState)message.State).PublishedAtUtc)
            .ThenBy(message => message.Id)
            .Limit(_settings.CleanupBatchSize)
            .Project(message => message.Id)
            .ToListAsync(cancellationToken);

        return await DeleteTerminalBatchAsync(
            ids,
            Builders<OutboxMessage>.Filter
                .OfType<OutboxMessageState, PublishedOutboxMessageState>(
                    message => message.State),
            cancellationToken);
    }

    private async Task<long> DeleteAbandonedBatchesAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken)
    {
        long totalDeleted = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deleted = await DeleteAbandonedBatchAsync(
                cutoffUtc,
                cancellationToken);
            totalDeleted += deleted;
            if (deleted < _settings.CleanupBatchSize)
                return totalDeleted;
        }
    }

    private async Task<long> DeleteAbandonedBatchAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken)
    {
        var filter = Builders<OutboxMessage>.Filter.And(
            Builders<OutboxMessage>.Filter
                .OfType<OutboxMessageState, AbandonedOutboxMessageState>(
                    message => message.State),
            Builders<OutboxMessage>.Filter.Lt(
                message => ((AbandonedOutboxMessageState)message.State).AbandonedAtUtc,
                cutoffUtc));
        var ids = await dbContext.OutboxMessages
            .Find(filter)
            .SortBy(message =>
                ((AbandonedOutboxMessageState)message.State).AbandonedAtUtc)
            .ThenBy(message => message.Id)
            .Limit(_settings.CleanupBatchSize)
            .Project(message => message.Id)
            .ToListAsync(cancellationToken);

        return await DeleteTerminalBatchAsync(
            ids,
            Builders<OutboxMessage>.Filter
                .OfType<OutboxMessageState, AbandonedOutboxMessageState>(
                    message => message.State),
            cancellationToken);
    }

    private async Task<long> DeleteTerminalBatchAsync(
        IReadOnlyCollection<string> ids,
        FilterDefinition<OutboxMessage> stateFilter,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return 0;

        var result = await dbContext.OutboxMessages.DeleteManyAsync(
            Builders<OutboxMessage>.Filter.And(
                stateFilter,
                Builders<OutboxMessage>.Filter.In(
                    message => message.Id,
                    ids)),
            cancellationToken);
        return result.DeletedCount;
    }

    private async Task UpdateQueueMetricsAsync(
        CancellationToken cancellationToken)
    {
        var pendingFilter = Builders<OutboxMessage>.Filter
            .OfType<OutboxMessageState, PendingOutboxMessageState>(
                message => message.State);
        var abandonedFilter = Builders<OutboxMessage>.Filter
            .OfType<OutboxMessageState, AbandonedOutboxMessageState>(
                message => message.State);
        var pendingCount = await dbContext.OutboxMessages.CountDocumentsAsync(
            pendingFilter,
            cancellationToken: cancellationToken);
        var abandonedCount = await dbContext.OutboxMessages.CountDocumentsAsync(
            abandonedFilter,
            cancellationToken: cancellationToken);
        var oldestPending = await dbContext.OutboxMessages
            .Find(pendingFilter)
            .SortBy(message => message.EnqueuedAtUtc)
            .Project(message => (DateTime?)message.EnqueuedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        var oldestPendingAge = oldestPending is null
            ? TimeSpan.Zero
            : timeProvider.GetUtcNow().UtcDateTime - oldestPending.Value;

        metrics.UpdateQueueSnapshot(
            pendingCount,
            abandonedCount,
            oldestPendingAge);
    }

    private static FilterDefinition<OutboxMessage> PendingDueFilter(
        DateTime nowUtc) =>
        Builders<OutboxMessage>.Filter.And(
            Builders<OutboxMessage>.Filter
                .OfType<OutboxMessageState, PendingOutboxMessageState>(
                    message => message.State),
            Builders<OutboxMessage>.Filter.Lte(
                message => ((PendingOutboxMessageState)message.State).NextAttemptAtUtc,
                nowUtc));

    private static FilterDefinition<OutboxMessage> PendingAttemptFilter(
        OutboxMessage message) =>
        Builders<OutboxMessage>.Filter.And(
            Builders<OutboxMessage>.Filter.Eq(
                item => item.Id,
                message.Id),
            Builders<OutboxMessage>.Filter
                .OfType<OutboxMessageState, PendingOutboxMessageState>(
                    item => item.State),
            Builders<OutboxMessage>.Filter.Eq(
                item => ((PendingOutboxMessageState)item.State).AttemptCount,
                PendingState(message).AttemptCount));

    private static PendingOutboxMessageState PendingState(
        OutboxMessage message) =>
        (PendingOutboxMessageState)message.State;
}
