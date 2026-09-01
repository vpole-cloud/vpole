using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.RedisServices.Events;

[EntityName("redis-service-updated")]
public sealed record RedisServiceUpdatedEvent(
    string RedisId,
    string Name,
    string ResourceGroupId,
    RedisServiceUpdatedEventChanges Changes,
    long ResourceRevision
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

public sealed record RedisServiceUpdatedEventChanges(
    UpdateLog<RedisServiceSpecSnapshot> RedisSpec,
    UpdateLog<ImmutableHashSet<string>> Tags);
