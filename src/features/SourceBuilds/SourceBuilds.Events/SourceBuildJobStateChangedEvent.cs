using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.SourceBuilds.Events;

[EntityName("pole.source-builds/v1/source-build-job-state-changed")]
public sealed record SourceBuildJobStateChangedEvent(
    string BuildJobId,
    string WebServiceId,
    string State,
    DateTime UpdatedAtUtc
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
