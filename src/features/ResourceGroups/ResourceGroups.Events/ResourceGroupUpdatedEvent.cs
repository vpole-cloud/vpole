using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;

namespace Pole.Features.ResourceGroups.Events;

 [EntityName("resource-group-updated")]
public sealed record ResourceGroupUpdatedEvent(
    string ResourceGroupId,
    string Name,
    string Description,
    ImmutableList<ShortResourceInfo> Resources,
    ResourceGroupUpdatedEventChanges Changes,
    long ResourceRevision
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

public sealed record ResourceGroupUpdatedEventChanges(
    UpdateLog<string> Description,
    UpdateLog<ImmutableHashSet<string>> Tags);
