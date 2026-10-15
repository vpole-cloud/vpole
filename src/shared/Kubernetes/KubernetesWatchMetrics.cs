using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Pole.Features.Core.Kubernetes;

public enum KubernetesWatchOutcome
{
    Listed,
    Opened,
    Eof,
    Expired,
    Malformed,
    StalledHeaders,
    Cancelled,
    Unavailable
}

/// <summary>Technical process-wide LIST/WATCH outcomes. Native selectors and metadata are excluded.</summary>
public sealed class KubernetesWatchMetrics : IDisposable
{
    public const string MeterName = "Pole.KubernetesWatch";

    internal static KubernetesWatchMetrics Shared
    {
        get;
    } = new();

    private readonly Meter meter;
    private readonly bool ownsMeter;
    private readonly Counter<long> outcomes;

    public KubernetesWatchMetrics(Meter? instrumentMeter = null)
    {
        meter = instrumentMeter ?? new(MeterName);
        ownsMeter = instrumentMeter is null;
        outcomes = meter.CreateCounter<long>("pole.kubernetes.watch.outcomes");
    }

    public void Dispose()
    {
        if (ownsMeter)
            meter.Dispose();
    }

    public void Record(KubernetesWatchOutcome outcome) => outcomes.Add(1,
        new TagList
        {
            {
                "recovery", outcome switch
                {
                    KubernetesWatchOutcome.Listed => "listed", KubernetesWatchOutcome.Opened => "opened",
                    KubernetesWatchOutcome.Eof => "eof",
                    KubernetesWatchOutcome.Expired => "410", KubernetesWatchOutcome.Malformed => "malformed",
                    KubernetesWatchOutcome.StalledHeaders => "stalled_headers",
                    KubernetesWatchOutcome.Cancelled => "cancelled", _ => "unavailable"
                }
            }
        });
}
