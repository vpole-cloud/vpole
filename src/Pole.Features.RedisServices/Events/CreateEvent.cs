using System.Collections.Immutable;
using MassTransit;
using Pole.Domain.Models;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.RedisServices.Events;

[EntityName("create-redis-service-requested")]
public record CreateEvent(
    string RedisId,
    string Name,
    string ResourceGroupId,
    RedisSpec Spec,
    ServiceStatus Status,
    ImmutableHashSet<string> Tags
) : BaseEvent;
