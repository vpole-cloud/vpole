using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Events;

[EntityName("delete-redis-service")]
public record DeleteEvent(
    string RedisId,
    string Name,
    string ResourceGroupId,
    RedisSpec Spec,
    ImmutableHashSet<string> Tags
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
