using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Events.RedisServices;

[EntityName("create-redis-service-requested")]
public record CreateEvent(
    string RedisId
) : BaseEvent;
