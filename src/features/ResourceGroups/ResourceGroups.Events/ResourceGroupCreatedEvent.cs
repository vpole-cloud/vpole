using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.Shared.Models;

namespace Pole.Features.ResourceGroups.Events;

[EntityName("resource-group-created")]
public record ResourceGroupCreatedEvent(
    string ResourceGroupId,
    string Name,
    string Description,
    ImmutableList<ShortResourceInfo> Resources,
    ImmutableHashSet<string> Tags
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
