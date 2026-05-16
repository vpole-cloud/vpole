using System.Collections.Immutable;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.RedisServices.Domain;
using Pole.Features.Shared.Models;

namespace Pole.Features.RedisServices.Events;

[EntityName("create-redis-service-requested")]
public record CreateEvent(
    string RedisId,
    string Name,
    string ResourceGroupId,
    RedisSpec Spec,
    ServiceStatus Status,
    ImmutableHashSet<string> Tags
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
