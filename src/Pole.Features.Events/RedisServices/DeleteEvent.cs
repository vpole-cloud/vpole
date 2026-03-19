using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Events.RedisServices;

[EntityName("delete-redis-service")]
public record DeleteEvent(
    string RedisId,
    string RedisName,
    string Namespace
) : BaseEvent;
