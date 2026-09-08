using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.Shared.Models;
using Xunit;

namespace Pole.Tests.Integration.Database;

public sealed class OutboxMessageDomainTests
{
    private const string MessageId = "507f1f77bcf86cd799439011";
    private const string PendingKey = "runtime-cell:507f1f77bcf86cd799439012:1:reconciliation";
    private static readonly DateTime OccurredAtUtc =
        new(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EnqueuedAtUtc = OccurredAtUtc.AddSeconds(1);

    [Fact]
    public void Factory_CreatesPendingMessage_WithNormalizedMetadata()
    {
        var message = CreateMessage(" pending-key ");

        Assert.Equal(MessageId, message.Id);
        Assert.Equal("pole.resource-groups/v1/runtime-cell-ready", message.EventType);
        Assert.Equal("tenant-id", message.TenantId);
        Assert.Equal("trace-id", message.TraceId);
        Assert.Equal("correlation-id", message.CorrelationId);
        Assert.Equal("pending-key", message.PendingKey);
        Assert.Equal(OccurredAtUtc, message.OccurredAtUtc);
        Assert.Equal(EnqueuedAtUtc, message.EnqueuedAtUtc);

        var pending = Assert.IsType<PendingOutboxMessageState>(message.State);
        Assert.Equal(0, pending.AttemptCount);
        Assert.Equal(EnqueuedAtUtc, pending.NextAttemptAtUtc);
        Assert.Null(pending.LastError);
    }

    [Fact]
    public void ScheduleRetry_RetainsPendingKey_AndIncrementsAttempt()
    {
        var message = CreateMessage(PendingKey);
        var nextAttemptAtUtc = EnqueuedAtUtc.AddMinutes(1);

        var retried = message.ScheduleRetry(nextAttemptAtUtc, " broker\r\n unavailable ");

        Assert.Equal(PendingKey, retried.PendingKey);
        var pending = Assert.IsType<PendingOutboxMessageState>(retried.State);
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal(nextAttemptAtUtc, pending.NextAttemptAtUtc);
        Assert.Equal("broker   unavailable", pending.LastError);
        Assert.IsType<PendingOutboxMessageState>(message.State);
    }

    [Fact]
    public void MarkPublished_ClearsPendingKey_AndProducesTerminalState()
    {
        var message = CreateMessage(PendingKey);
        var publishedAtUtc = EnqueuedAtUtc.AddMinutes(1);

        var published = message.MarkPublished(publishedAtUtc);

        Assert.Null(published.PendingKey);
        var state = Assert.IsType<PublishedOutboxMessageState>(published.State);
        Assert.Equal(publishedAtUtc, state.PublishedAtUtc);
        Assert.Throws<InvalidOperationException>(() =>
            published.ScheduleRetry(publishedAtUtc.AddMinutes(1), "retry"));
    }

    [Fact]
    public void Abandon_ClearsPendingKey_AndPreservesAttemptCount()
    {
        var message = CreateMessage(PendingKey)
            .ScheduleRetry(EnqueuedAtUtc.AddMinutes(1), "temporary failure");
        var abandonedAtUtc = EnqueuedAtUtc.AddMinutes(2);

        var abandoned = message.Abandon(
            abandonedAtUtc,
            " InvalidPayload ",
            " malformed\u0000payload ");

        Assert.Null(abandoned.PendingKey);
        var state = Assert.IsType<AbandonedOutboxMessageState>(abandoned.State);
        Assert.Equal(1, state.AttemptCount);
        Assert.Equal(abandonedAtUtc, state.AbandonedAtUtc);
        Assert.Equal("InvalidPayload", state.FailureCode);
        Assert.Equal("malformed payload", state.LastError);
        Assert.Throws<InvalidOperationException>(() =>
            abandoned.MarkPublished(abandonedAtUtc.AddMinutes(1)));
    }

    [Fact]
    public void PolymorphicState_RoundTripsThroughBson()
    {
        var message = CreateMessage(PendingKey)
            .ScheduleRetry(EnqueuedAtUtc.AddMinutes(1), "broker unavailable");

        var roundTripped = BsonSerializer.Deserialize<OutboxMessage>(message.ToBson());

        Assert.Equal(message, roundTripped);
        Assert.IsType<PendingOutboxMessageState>(roundTripped.State);
    }

    [Fact]
    public void Factory_RejectsInvalidTimeline()
    {
        var options = new OutboxMessageFactory.CreateOptions(
            MessageId,
            OccurredAtUtc.AddSeconds(-1));

        Assert.Throws<ArgumentException>(() => OutboxMessageFactory.Create(
            "pole.resource-groups/v1/runtime-cell-ready",
            "tenant-id",
            "{}",
            "trace-id",
            "correlation-id",
            OccurredAtUtc,
            PendingKey,
            options));
    }

    [Fact]
    public void Retry_RejectsTimeMovingBackwards()
    {
        var message = CreateMessage(PendingKey)
            .ScheduleRetry(EnqueuedAtUtc.AddMinutes(1), "broker unavailable");

        Assert.Throws<ArgumentException>(() =>
            message.ScheduleRetry(EnqueuedAtUtc.AddSeconds(30), "broker unavailable"));
    }

    private static OutboxMessage CreateMessage(string? pendingKey) =>
        OutboxMessageFactory.Create(
            " pole.resource-groups/v1/runtime-cell-ready ",
            " tenant-id ",
            "{}",
            " trace-id ",
            " correlation-id ",
            OccurredAtUtc,
            pendingKey,
            new OutboxMessageFactory.CreateOptions(MessageId, EnqueuedAtUtc));
}
