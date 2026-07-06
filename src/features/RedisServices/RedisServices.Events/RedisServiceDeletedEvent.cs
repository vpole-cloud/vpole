using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.RedisServices.Events;

[EntityName("redis-service-deleted")]
public record RedisServiceDeletedEvent(
    string RedisId,
    string Name,
    string ResourceGroupId,
    RedisServiceSpecSnapshot Spec,
    ImmutableHashSet<string> Tags
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
