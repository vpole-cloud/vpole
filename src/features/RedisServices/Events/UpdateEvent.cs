using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Events;

[EntityName("update-redis-service")]
public sealed record UpdateEvent(
    string RedisId,
    string Name,
    string ResourceGroupId,
    UpdateEventChanges Changes
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}

public sealed record UpdateEventChanges(
    UpdateLog<RedisSpec> RedisSpec,
    UpdateLog<ImmutableHashSet<string>> Tags);
