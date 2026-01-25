using MassTransit;
using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.RedisServices.Events;

[EntityName("update-redis-service")]
public sealed record UpdateEvent : BaseEvent
{
    public required string RedisId { get; init; }
}
