using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Pole.Features.Outbox.Application;

public sealed class OutboxMetrics : IDisposable
{
    public const string MeterName = "Pole.Outbox";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _published;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _abandoned;
    private readonly Counter<long> _cleaned;
    private readonly Histogram<double> _publishDuration;
    private readonly Histogram<double> _retryDelay;
    private long _pendingCount;
    private long _abandonedCount;
    private double _oldestPendingAgeSeconds;

    public OutboxMetrics()
    {
        _published = _meter.CreateCounter<long>(
            "pole.outbox.messages.published",
            description: "Number of outbox messages published.");
        _retries = _meter.CreateCounter<long>(
            "pole.outbox.messages.retries",
            description: "Number of outbox message retries scheduled.");
        _abandoned = _meter.CreateCounter<long>(
            "pole.outbox.messages.abandoned",
            description: "Number of structurally invalid outbox messages abandoned.");
        _cleaned = _meter.CreateCounter<long>(
            "pole.outbox.messages.cleaned",
            description: "Number of terminal outbox messages removed.");
        _publishDuration = _meter.CreateHistogram<double>(
            "pole.outbox.publish.duration",
            unit: "s",
            description: "Outbox publish duration.");
        _retryDelay = _meter.CreateHistogram<double>(
            "pole.outbox.retry.delay",
            unit: "s",
            description: "Scheduled outbox retry delay.");
        _meter.CreateObservableGauge(
            "pole.outbox.messages.pending",
            () => Interlocked.Read(ref _pendingCount),
            description: "Current number of pending outbox messages.");
        _meter.CreateObservableGauge(
            "pole.outbox.messages.abandoned.current",
            () => Interlocked.Read(ref _abandonedCount),
            description: "Current number of abandoned outbox messages.");
        _meter.CreateObservableGauge(
            "pole.outbox.pending.oldest_age",
            () => Volatile.Read(ref _oldestPendingAgeSeconds),
            unit: "s",
            description: "Age of the oldest pending outbox message.");
    }

    public void RecordPublished(string eventType, TimeSpan duration)
    {
        var tags = new TagList { { "event.type", eventType } };
        _published.Add(1, tags);
        _publishDuration.Record(duration.TotalSeconds, tags);
    }

    public void RecordRetry(
        string eventType,
        TimeSpan delay)
    {
        var tags = new TagList { { "event.type", eventType } };
        _retries.Add(1, tags);
        _retryDelay.Record(delay.TotalSeconds, tags);
    }

    public void RecordAbandoned(string eventType, string failureCode) =>
        _abandoned.Add(
            1,
            new TagList
            {
                { "event.type", eventType },
                { "failure.code", failureCode }
            });

    public void RecordCleaned(string state, long count)
    {
        if (count == 0)
            return;

        _cleaned.Add(count, new TagList { { "state", state } });
    }

    public void UpdateQueueSnapshot(
        long pendingCount,
        long abandonedCount,
        TimeSpan oldestPendingAge)
    {
        Interlocked.Exchange(ref _pendingCount, pendingCount);
        Interlocked.Exchange(ref _abandonedCount, abandonedCount);
        Volatile.Write(
            ref _oldestPendingAgeSeconds,
            Math.Max(0, oldestPendingAge.TotalSeconds));
    }

    public void Dispose() => _meter.Dispose();
}
