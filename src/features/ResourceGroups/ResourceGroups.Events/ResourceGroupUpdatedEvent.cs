using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("pole.resource-groups/v1/resource-group-updated")]
public sealed record ResourceGroupUpdatedEvent(
    string ResourceGroupId,
    string Name,
    string Description,
    ResourceGroupUpdatedEventChanges Changes,
    long ResourceRevision
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

public sealed record ResourceGroupUpdatedEventChanges(
    UpdateLog<string> Description,
    UpdateLog<ImmutableHashSet<string>> Tags);
