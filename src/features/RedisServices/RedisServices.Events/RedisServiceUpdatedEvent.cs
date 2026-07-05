using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Events;

[EntityName("redis-service-updated")]
public sealed record RedisServiceUpdatedEvent(
    string RedisId,
    string Name,
    string ResourceGroupId,
    RedisServiceUpdatedEventChanges Changes
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

public sealed record RedisServiceUpdatedEventChanges(
    UpdateLog<RedisSpec> RedisSpec,
    UpdateLog<ImmutableHashSet<string>> Tags);
