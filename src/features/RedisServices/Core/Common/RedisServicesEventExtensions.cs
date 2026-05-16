using Pole.Features.Events.Abstractions;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Events;

namespace Pole.Features.RedisServices.Common;

internal static class RedisServicesEventExtensions
{
    extension(Redis redis)
    {
        internal CreateEvent ToCreateEvent()
            => new(
                redis.Id,
                redis.Name,
                redis.ResourceGroupId,
                redis.Spec,
                redis.Status,
                redis.Tags)
            {
                EventCore = EventCore.Create(redis.TenantId)
            };

        internal DeleteEvent ToDeleteEvent()
            => new(
                redis.Id,
                redis.Name,
                redis.ResourceGroupId,
                redis.Spec,
                redis.Tags)
            {
                EventCore = EventCore.Create(redis.TenantId)
            };
    }

    extension(CreateEvent createEvent)
    {
        internal Redis ToRedis()
            => RedisFactory.Create(
                createEvent.EventCore.TenantId,
                createEvent.Name,
                createEvent.ResourceGroupId,
                createEvent.Spec,
                RedisFactory.DefaultCreateOptions with
                {
                    Id = createEvent.RedisId,
                    Status = createEvent.Status,
                    Tags = createEvent.Tags
                });
    }
}
