using System.Diagnostics.Metrics;
using Pole.Features.Outbox.Application;
using Xunit;

namespace Pole.Tests.Integration.Outbox;

public sealed class OutboxMetricsTests
{
    [Fact]
    public void DeliveryCounters_UseOnlyBoundedOperationalTags()
    {
        var measurements = new List<MeasurementSnapshot>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == OutboxMetrics.MeterName)
                    meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, value, tags, _) =>
            {
                measurements.Add(new MeasurementSnapshot(
                    instrument.Name,
                    value,
                    tags.ToArray()));
            });
        listener.Start();
        using var metrics = new OutboxMetrics();

        metrics.RecordPublished(
            "pole.resource-groups/v1/runtime-cell-ready",
            TimeSpan.FromMilliseconds(5));
        metrics.RecordRetry(
            "pole.resource-groups/v1/runtime-cell-ready",
            TimeSpan.FromSeconds(15));
        metrics.RecordAbandoned("unknown", "UnknownEventType");
        metrics.RecordCleaned("published", 3);

        Assert.Contains(measurements, measurement =>
            measurement.Name == "pole.outbox.messages.published");
        Assert.Contains(measurements, measurement =>
            measurement.Name == "pole.outbox.messages.retries");
        Assert.Contains(measurements, measurement =>
            measurement.Name == "pole.outbox.messages.abandoned");
        Assert.Contains(measurements, measurement =>
            measurement.Name == "pole.outbox.messages.cleaned" &&
            measurement.Value == 3);
        Assert.All(
            measurements.SelectMany(measurement => measurement.Tags),
            tag => Assert.Contains(
                tag.Key,
                new[] { "event.type", "failure.code", "state" }));
    }

    private sealed record MeasurementSnapshot(
        string Name,
        long Value,
        KeyValuePair<string, object?>[] Tags);
}
