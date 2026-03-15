using MassTransit;
using Pole.Features.Core.Abstractions;

namespace Pole.Features.RedisServices.Events;

[EntityName("delete-redis-service")]
public record DeleteEvent(
    string RedisId,
    string RedisName,
    string Namespace
) : BaseEvent;
