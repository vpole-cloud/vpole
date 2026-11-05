using Pole.Features.ResourceConnections.Application.References;
using Pole.Features.ResourceConnections.Application.Recovery;
using Pole.Features.ResourceConnections.Application.Mongo;
using Pole.Features.ResourceConnections.Application.Declarations;
using Pole.Features.ResourceConnections.Application.Connections;
using Pole.Features.ResourceConnections.Application.Common;
namespace Pole.Features.ResourceConnections.Application.Topology;

internal enum ConnectionTopologyKind { Eligible, SourceHasNoPlacements, DestinationTargetMissing, DestinationTargetAmbiguous }
internal sealed record ConnectionTopologyAssessment(
    ConnectionTopologyKind Kind,
    string? TargetId = null);

internal static class ResourceConnectionTopology
{
    internal static ConnectionTopologyAssessment Evaluate(IEnumerable<string> sourceTargets, IEnumerable<string> destinationTargets)
    {
        var sources = sourceTargets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (sources.Length == 0) return new(ConnectionTopologyKind.SourceHasNoPlacements);
        var destinations = destinationTargets.GroupBy(target => target, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (!destinations.TryGetValue(source, out var count)) return new(ConnectionTopologyKind.DestinationTargetMissing, source);
            if (count != 1) return new(ConnectionTopologyKind.DestinationTargetAmbiguous, source);
        }
        return new(ConnectionTopologyKind.Eligible);
    }
}
