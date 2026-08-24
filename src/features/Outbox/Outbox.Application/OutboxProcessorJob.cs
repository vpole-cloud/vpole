using System.Text.Json;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Application.Persistence;
using Pole.Features.Shared.Models;
using Pole.Observability;
using EventExtensions = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Features.Outbox.Application;

/// <summary>
/// Delivers durable messages with at-least-once semantics. A publish can be
/// repeated when the process stops before the pending-to-published transition.
/// </summary>
public sealed class OutboxProcessorJob(
    LoadDueOutboxMessages loadDueOutboxMessages,
    TryPersistOutboxMessageTransition tryPersistTransition,
    CleanupTerminalOutboxMessages cleanupTerminalMessages,
    LoadOutboxQueueSnapshot loadQueueSnapshot,
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
        var messages = await loadDueOutboxMessages(
            nowUtc,
            _settings.BatchSize,
            cancellationToken);

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

        var cleanup = await cleanupTerminalMessages(
            new OutboxCleanupRequest(
                publishedCutoffUtc,
                abandonedCutoffUtc,
                _settings.CleanupBatchSize),
            cancellationToken);

        metrics.RecordCleaned("published", cleanup.PublishedCount);
        metrics.RecordCleaned("abandoned", cleanup.AbandonedCount);
        if (cleanup.PublishedCount + cleanup.AbandonedCount > 0)
        {
            logger.LogInformation(
                "Cleaned {PublishedCount} published and {AbandonedCount} abandoned outbox messages",
                cleanup.PublishedCount,
                cleanup.AbandonedCount);
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
        if (!await tryPersistTransition(message, published, cancellationToken))
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

        if (!await tryPersistTransition(message, retried, cancellationToken))
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
        if (!await tryPersistTransition(message, abandoned, cancellationToken))
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

    private async Task UpdateQueueMetricsAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await loadQueueSnapshot(cancellationToken);
        var oldestPendingAge = snapshot.OldestPendingAtUtc is null
            ? TimeSpan.Zero
            : timeProvider.GetUtcNow().UtcDateTime - snapshot.OldestPendingAtUtc.Value;

        metrics.UpdateQueueSnapshot(
            snapshot.PendingCount,
            snapshot.AbandonedCount,
            oldestPendingAge);
    }

    private static PendingOutboxMessageState PendingState(
        OutboxMessage message) =>
        (PendingOutboxMessageState)message.State;
}
