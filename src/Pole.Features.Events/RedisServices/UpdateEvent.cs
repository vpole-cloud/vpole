using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Events.RedisServices;

[EntityName("update-redis-service")]
public sealed record UpdateEvent : BaseEvent
{
    public required string RedisId { get; init; }
}
