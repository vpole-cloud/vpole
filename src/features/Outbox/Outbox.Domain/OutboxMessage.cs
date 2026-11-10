using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.Outbox.Domain;

public sealed record OutboxMessage(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string EventType,
    string? TenantId,
    string PayloadJson,
    string? TraceId,
    string? CorrelationId,
    DateTime OccurredAtUtc,
    DateTime EnqueuedAtUtc,
    string? PendingKey,
    OutboxMessageState State
)
{
    public static OutboxMessage Create(
        string eventType,
        string? tenantId,
        string payloadJson,
        string? traceId,
        string? correlationId,
        DateTime occurredAtUtc,
        DateTime enqueuedAtUtc,
        string? pendingKey) => OutboxMessageFactory.Create(
        eventType,
        tenantId,
        payloadJson,
        traceId,
        correlationId,
        occurredAtUtc,
        pendingKey,
        new OutboxMessageFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), enqueuedAtUtc));

    public OutboxMessage MarkPublished(DateTime publishedAtUtc)
    {
        EnsurePending();
        var normalizedPublishedAtUtc = OutboxMessageValueGuard.TransitionUtc(
            publishedAtUtc,
            EnqueuedAtUtc,
            nameof(publishedAtUtc));

        return WithState(pendingKey: null, new PublishedOutboxMessageState(normalizedPublishedAtUtc));
    }

    public OutboxMessage ScheduleRetry(DateTime nextAttemptAtUtc, string lastError)
    {
        var pending = EnsurePending();
        var normalizedNextAttemptAtUtc = OutboxMessageValueGuard.TransitionUtc(
            nextAttemptAtUtc,
            pending.NextAttemptAtUtc,
            nameof(nextAttemptAtUtc));

        return WithState(
            PendingKey,
            new PendingOutboxMessageState(
                checked(pending.AttemptCount + 1),
                normalizedNextAttemptAtUtc,
                OutboxMessageValueGuard.Error(lastError, nameof(lastError))));
    }

    public OutboxMessage Abandon(
        DateTime abandonedAtUtc,
        string failureCode,
        string lastError)
    {
        var pending = EnsurePending();
        var normalizedAbandonedAtUtc = OutboxMessageValueGuard.TransitionUtc(
            abandonedAtUtc,
            EnqueuedAtUtc,
            nameof(abandonedAtUtc));

        return WithState(
            pendingKey: null,
            new AbandonedOutboxMessageState(
                pending.AttemptCount,
                normalizedAbandonedAtUtc,
                OutboxMessageValueGuard.Required(
                    failureCode,
                    nameof(failureCode),
                    OutboxMessageValueGuard.MaximumFailureCodeLength),
                OutboxMessageValueGuard.Error(lastError, nameof(lastError))));
    }

    private PendingOutboxMessageState EnsurePending()
    {
        if (State is PendingOutboxMessageState pending)
            return pending;

        throw new InvalidOperationException(
            $"Cannot transition an outbox message from terminal state '{State.GetType().Name}'.");
    }

    private OutboxMessage WithState(string? pendingKey, OutboxMessageState state) => new(
        Id,
        EventType,
        TenantId,
        PayloadJson,
        TraceId,
        CorrelationId,
        OccurredAtUtc,
        EnqueuedAtUtc,
        pendingKey,
        state);
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(PendingOutboxMessageState),
    typeof(PublishedOutboxMessageState),
    typeof(AbandonedOutboxMessageState))]
public abstract record OutboxMessageState;

[BsonDiscriminator("pending")]
public sealed record PendingOutboxMessageState(
    int AttemptCount,
    DateTime NextAttemptAtUtc,
    string? LastError
) : OutboxMessageState;

[BsonDiscriminator("published")]
public sealed record PublishedOutboxMessageState(
    DateTime PublishedAtUtc
) : OutboxMessageState;

[BsonDiscriminator("abandoned")]
public sealed record AbandonedOutboxMessageState(
    int AttemptCount,
    DateTime AbandonedAtUtc,
    string FailureCode,
    string LastError
) : OutboxMessageState;

public static class OutboxMessageFactory
{
    public static CreateOptions DefaultOptions => new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow);

    public static OutboxMessage Create(
        string eventType,
        string? tenantId,
        string payloadJson,
        string? traceId,
        string? correlationId,
        DateTime occurredAtUtc,
        string? pendingKey,
        CreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var normalizedOccurredAtUtc = OutboxMessageValueGuard.Utc(occurredAtUtc, nameof(occurredAtUtc));
        var normalizedEnqueuedAtUtc = OutboxMessageValueGuard.Utc(options.EnqueuedAtUtc, nameof(options.EnqueuedAtUtc));
        if (normalizedEnqueuedAtUtc < normalizedOccurredAtUtc)
            throw new ArgumentException(
                "An outbox message cannot be enqueued before its event occurred.",
                nameof(options));

        return new OutboxMessage(
            OutboxMessageValueGuard.ObjectId(options.Id, nameof(options.Id)),
            OutboxMessageValueGuard.Required(
                eventType,
                nameof(eventType),
                OutboxMessageValueGuard.MaximumEventTypeLength),
            OutboxMessageValueGuard.Optional(tenantId, nameof(tenantId), OutboxMessageValueGuard.MaximumTenantIdLength),
            OutboxMessageValueGuard.Payload(payloadJson, nameof(payloadJson)),
            OutboxMessageValueGuard.Optional(traceId, nameof(traceId), OutboxMessageValueGuard.MaximumTraceIdLength),
            OutboxMessageValueGuard.Optional(
                correlationId,
                nameof(correlationId),
                OutboxMessageValueGuard.MaximumCorrelationIdLength),
            normalizedOccurredAtUtc,
            normalizedEnqueuedAtUtc,
            OutboxMessageValueGuard.Optional(
                pendingKey,
                nameof(pendingKey),
                OutboxMessageValueGuard.MaximumPendingKeyLength),
            new PendingOutboxMessageState(AttemptCount: 0, normalizedEnqueuedAtUtc, LastError: null));
    }

    public sealed record CreateOptions(
        string Id,
        DateTime EnqueuedAtUtc
    );
}

internal static class OutboxMessageValueGuard
{
    public const int MaximumEventTypeLength = 512;
    public const int MaximumTenantIdLength = 128;
    public const int MaximumTraceIdLength = 128;
    public const int MaximumCorrelationIdLength = 256;
    public const int MaximumPendingKeyLength = 512;
    public const int MaximumFailureCodeLength = 128;
    public const int MaximumErrorLength = 2048;

    public static string Required(
        string? value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value is required.", parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
            throw new ArgumentException($"The value must be {maximumLength} characters or less.", parameterName);

        return normalized;
    }

    public static string? Optional(
        string? value,
        string parameterName,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Required(value, parameterName, maximumLength);
    }

    public static string ObjectId(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, 24);
        if (!MongoDB.Bson.ObjectId.TryParse(normalized, out _))
            throw new ArgumentException("The value must be a MongoDB ObjectId.", parameterName);

        return normalized;
    }

    public static string Payload(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The event payload is required.", parameterName);

        return value;
    }

    public static string Error(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("An error message is required.", parameterName);

        var sanitized = string.Concat(value.Trim().Select(character => char.IsControl(character) ? ' ' : character))
            .Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new ArgumentException("An error message is required.", parameterName);

        return sanitized.Length <= MaximumErrorLength ? sanitized : sanitized[..MaximumErrorLength];
    }

    public static DateTime Utc(DateTime value, string parameterName)
    {
        if (value == default)
            throw new ArgumentException("The timestamp is required.", parameterName);

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    }

    public static DateTime TransitionUtc(
        DateTime value,
        DateTime earliestUtc,
        string parameterName)
    {
        var normalized = Utc(value, parameterName);
        if (normalized < earliestUtc)
            throw new ArgumentException("The transition time cannot move backwards.", parameterName);

        return normalized;
    }
}
