// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.ResourceConnections.Application.Topology;

internal enum ConnectionTopologyKind
{
    Eligible, SourceHasNoPlacements, DestinationTargetMissing, DestinationTargetAmbiguous,
}

internal sealed record ConnectionTopologyAssessment(
    ConnectionTopologyKind Kind,
    string? TargetId = null
);

internal static class ResourceConnectionTopology
{
    internal static ConnectionTopologyAssessment Evaluate(
        IEnumerable<string> sourceTargets,
        IEnumerable<string> destinationTargets)
    {
        var sources = sourceTargets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (sources.Length == 0)
            return new ConnectionTopologyAssessment(ConnectionTopologyKind.SourceHasNoPlacements);
        var destinations = destinationTargets.GroupBy(target => target, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (!destinations.TryGetValue(source, out var count))
                return new ConnectionTopologyAssessment(ConnectionTopologyKind.DestinationTargetMissing, source);
            if (count != 1)
                return new ConnectionTopologyAssessment(ConnectionTopologyKind.DestinationTargetAmbiguous, source);
        }

        return new ConnectionTopologyAssessment(ConnectionTopologyKind.Eligible);
    }
}
