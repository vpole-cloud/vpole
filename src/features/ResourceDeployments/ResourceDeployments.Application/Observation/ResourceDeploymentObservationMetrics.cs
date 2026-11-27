// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Pole.Features.ResourceDeployments.Application.Observation;

public enum ObservationReadOutcome
{
    Shared,
    Live,
    Unavailable,
}

public enum ObservationSampleOutcome
{
    Accepted,
    Rejected,
    Cancelled,
    Failed,
}

/// <summary>Feature-owned aggregate observations with no workload or target labels.</summary>
public sealed class ResourceDeploymentObservationMetrics : IDisposable
{
    public const string MeterName = "Pole.ResourceDeploymentObservations";
    private readonly Histogram<double> duration;
    private readonly Meter meter;
    private readonly bool ownsMeter;
    private readonly Counter<long> reads;
    private readonly Counter<long> samples;
    private readonly Counter<long> sources;

    public ResourceDeploymentObservationMetrics(Meter? instrumentMeter = null)
    {
        meter = instrumentMeter ?? new Meter(MeterName);
        ownsMeter = instrumentMeter is null;
        reads = meter.CreateCounter<long>("pole.observation.reads");
        sources = meter.CreateCounter<long>("pole.observation.sources.transitions");
        samples = meter.CreateCounter<long>("pole.observation.samples");
        duration = meter.CreateHistogram<double>("pole.observation.sample.duration", "s");
    }

    public void Dispose()
    {
        if (ownsMeter)
            meter.Dispose();
    }

    public void RecordRead(ObservationReadOutcome outcome) => reads.Add(
        1,
        new TagList
        {
            {
                "outcome",
                outcome switch
                {
                    ObservationReadOutcome.Shared => "shared", ObservationReadOutcome.Live => "live",
                    _ => "unavailable",
                }
            },
        });

    public void RecordSource(bool healthy) =>
        sources.Add(1, new TagList { { "outcome", healthy ? "gained" : "lost" } });

    public void RecordSample(ObservationSampleOutcome outcome, TimeSpan elapsed)
    {
        var tags = new TagList
        {
            {
                "outcome",
                outcome switch
                {
                    ObservationSampleOutcome.Accepted => "accepted", ObservationSampleOutcome.Rejected => "rejected",
                    ObservationSampleOutcome.Cancelled => "cancelled", _ => "failed",
                }
            },
        };
        samples.Add(1, tags);
        duration.Record(elapsed.TotalSeconds, tags);
    }
}
