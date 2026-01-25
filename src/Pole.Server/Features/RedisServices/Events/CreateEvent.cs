using MassTransit;
using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.RedisServices.Events;

[EntityName("create-redis-service-requested")]
public record CreateEvent(
    string RedisId
) : BaseEvent;
