using MassTransit;
using Pole.Features.Core.Abstractions;

namespace Pole.Features.RedisServices.Events;

[EntityName("update-redis-service")]
public sealed record UpdateEvent : BaseEvent
{
    public required string RedisId { get; init; }
}
