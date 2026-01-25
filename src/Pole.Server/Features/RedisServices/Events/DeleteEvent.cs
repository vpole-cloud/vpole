using MassTransit;
using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.RedisServices.Events;

[EntityName("delete-redis-service")]
public record DeleteEvent(
    string RedisId,
    string RedisName,
    string Namespace
) : BaseEvent;
