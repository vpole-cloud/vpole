using System.Collections.Immutable;
using MassTransit;
using Pole.Domain.Models;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.RedisServices.Events;

[EntityName("delete-redis-service")]
public record DeleteEvent(
    string RedisId,
    string Name,
    string ResourceGroupId,
    RedisSpec Spec,
    ImmutableHashSet<string> Tags
) : BaseEvent;
