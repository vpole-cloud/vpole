using Pole.Domain.Models;
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
                redis.Tags);

        internal DeleteEvent ToDeleteEvent()
            => new(
                redis.Id,
                redis.Name,
                redis.ResourceGroupId,
                redis.Spec,
                redis.Tags);
    }

    extension(CreateEvent createEvent)
    {
        internal Redis ToRedis()
            => RedisFactory.Create(
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
