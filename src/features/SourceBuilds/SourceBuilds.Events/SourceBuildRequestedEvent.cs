using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.SourceBuilds.Events;

[EntityName("source-build-requested")]
public sealed record SourceBuildRequestedEvent(string BuildJobId) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
