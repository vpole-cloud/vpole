using System.Collections.Immutable;
using MassTransit;
using Pole.Domain.Models;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.RedisServices.Events;

[EntityName("update-redis-service")]
public sealed record UpdateEvent(
    string RedisId,
    UpdateEventChanges Changes
) : BaseEvent;

public sealed record UpdateEventChanges(
    UpdateLog<RedisSpec> RedisSpec,
    UpdateLog<ImmutableHashSet<string>> Tags);
