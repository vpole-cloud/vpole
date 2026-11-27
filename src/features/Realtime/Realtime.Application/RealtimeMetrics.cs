// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Pole.Features.Realtime.Contracts;

namespace Pole.Features.Realtime.Application;

public enum RealtimeDeliveryOutcome
{
    Sent,
    NoRecipients,
    Unavailable,
    Failed,
    Cancelled,
}

/// <summary>Aggregate operational measurements; recipient identities never become labels.</summary>
public sealed class RealtimeMetrics : IDisposable
{
    public const string MeterName = "Pole.Realtime";
    private readonly Counter<long> connections;
    private readonly Counter<long> coverage;
    private readonly Counter<long> deliveryAttempts;
    private readonly Histogram<double> deliveryDuration;
    private readonly Counter<long> fallbacks;
    private readonly Meter meter;
    private readonly bool ownsMeter;
    private readonly Counter<long> recipients;
    private readonly Counter<long> reconnects;
    private readonly Counter<long> subscriptions;

    public RealtimeMetrics(Meter? instrumentMeter = null)
    {
        meter = instrumentMeter ?? new Meter(MeterName);
        ownsMeter = instrumentMeter is null;
        deliveryAttempts = meter.CreateCounter<long>("pole.realtime.delivery.attempts");
        recipients = meter.CreateCounter<long>("pole.realtime.delivery.recipients");
        deliveryDuration = meter.CreateHistogram<double>(
            "pole.realtime.delivery.duration",
            "s",
            "Server authorization and SignalR send duration; not browser delivery latency.");
        connections = meter.CreateCounter<long>("pole.realtime.connections.transitions");
        subscriptions = meter.CreateCounter<long>("pole.realtime.subscriptions.transitions");
        coverage = meter.CreateCounter<long>("pole.realtime.coverage.transitions");
        reconnects = meter.CreateCounter<long>("pole.realtime.reconnects");
        fallbacks = meter.CreateCounter<long>("pole.realtime.fallbacks");
    }

    public void Dispose()
    {
        if (ownsMeter)
            meter.Dispose();
    }

    public void RecordDelivery(
        RealtimeTopic? topic,
        RealtimeDeliveryOutcome outcome,
        int recipientCount,
        TimeSpan duration)
    {
        var tags = Tags(
            topic,
            outcome switch
            {
                RealtimeDeliveryOutcome.Sent => "sent",
                RealtimeDeliveryOutcome.NoRecipients => "no_recipients",
                RealtimeDeliveryOutcome.Unavailable => "unavailable",
                RealtimeDeliveryOutcome.Cancelled => "cancelled",
                _ => "failed",
            });
        deliveryAttempts.Add(1, tags);
        if (recipientCount > 0)
            recipients.Add(recipientCount, tags);
        deliveryDuration.Record(duration.TotalSeconds, tags);
    }

    public void RecordConnection(bool connected) => connections.Add(
        1,
        new TagList { { "outcome", connected ? "connected" : "disconnected" } });

    public void RecordSubscription(RealtimeTopic? topic, bool added) =>
        subscriptions.Add(1, Tags(topic, added ? "subscribed" : "released"));

    public void RecordCoverage(RealtimeTopic? topic, bool covered)
    {
        coverage.Add(1, Tags(topic, covered ? "covered" : "uncovered"));
        if (!covered)
            fallbacks.Add(1, new TagList { { "source", "server" } });
    }

    public void RecordClientLifecycle(RealtimeClientLifecycleReport report)
    {
        var tags = new TagList { { "source", "client" } };
        if (report.Reconnects > 0)
            reconnects.Add(report.Reconnects, tags);
        if (report.Fallbacks > 0)
            fallbacks.Add(report.Fallbacks, tags);
    }

    private static TagList Tags(RealtimeTopic? topic, string outcome) => new()
    {
        { "topic", topic?.Name ?? "unknown" },
        {
            "scope",
            topic?.Scope switch
            {
                RealtimeScope.Tenant => "tenant", RealtimeScope.Platform => "platform",
                RealtimeScope.User => "user", _ => "unknown",
            }
        },
        { "outcome", outcome },
    };
}
