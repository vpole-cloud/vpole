using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("resource-group-deleted")]
public record ResourceGroupDeletedEvent(
    string ResourceGroupId,
    string Name,
    string Description,
    ImmutableHashSet<string> Tags,
    long ResourceRevision
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
