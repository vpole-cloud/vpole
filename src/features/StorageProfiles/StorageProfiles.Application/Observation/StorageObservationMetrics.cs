using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Pole.Features.StorageProfiles.Application.Observation;

public enum StorageObservationReadOutcome { Shared, Live, Unavailable }
public enum StorageObservationReadKind { Coherent, Classes, Volumes, Claims }
public enum StorageObservationSampleOutcome { Accepted, Rejected, Cancelled, Failed }

/// <summary>Aggregate storage observation outcomes with fixed labels and no target or native metadata.</summary>
public sealed class StorageObservationMetrics : IDisposable
{
    public const string MeterName = "Pole.StorageObservations";
    private readonly Meter meter;
    private readonly bool ownsMeter;
    private readonly Counter<long> reads;
    private readonly Counter<long> sources;
    private readonly Counter<long> samples;
    private readonly Histogram<double> duration;
    public StorageObservationMetrics(Meter? instrumentMeter = null)
    {
        meter = instrumentMeter ?? new(MeterName);
        ownsMeter = instrumentMeter is null;
        reads = meter.CreateCounter<long>("pole.storage.observation.reads");
        sources = meter.CreateCounter<long>("pole.storage.observation.sources.transitions");
        samples = meter.CreateCounter<long>("pole.storage.observation.samples");
        duration = meter.CreateHistogram<double>("pole.storage.observation.sample.duration", "s");
    }
    public void RecordRead(StorageObservationReadOutcome outcome, StorageObservationReadKind kind = StorageObservationReadKind.Coherent) => reads.Add(1, new TagList
    {
        { "outcome", outcome switch { StorageObservationReadOutcome.Shared => "shared", StorageObservationReadOutcome.Live => "live", _ => "unavailable" } },
        { "kind", kind switch { StorageObservationReadKind.Classes => "classes", StorageObservationReadKind.Volumes => "volumes", StorageObservationReadKind.Claims => "claims", _ => "coherent" } }
    });
    public void RecordSource(bool healthy) => sources.Add(1, new TagList { { "outcome", healthy ? "gained" : "lost" } });
    public void RecordSample(StorageObservationSampleOutcome outcome, TimeSpan elapsed)
    {
        var tags = new TagList { { "outcome", outcome switch { StorageObservationSampleOutcome.Accepted => "accepted", StorageObservationSampleOutcome.Rejected => "rejected", StorageObservationSampleOutcome.Cancelled => "cancelled", _ => "failed" } } };
        samples.Add(1, tags); duration.Record(elapsed.TotalSeconds, tags);
    }
    public void Dispose() { if (ownsMeter) meter.Dispose(); }
}
