using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Search.Events;

[EntityName("pole.search/v1/search-projection-changed")]
public sealed record SearchProjectionChangedEvent(
    string SourceId,
    string ResourceType,
    long ResourceRevision,
    long? ObservedGeneration = null) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
