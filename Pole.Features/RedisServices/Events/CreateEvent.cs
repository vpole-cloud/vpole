using MassTransit;
using Pole.Features.Core.Abstractions;

namespace Pole.Features.RedisServices.Events;

[EntityName("create-redis-service-requested")]
public record CreateEvent(
    string RedisId
) : BaseEvent;
