using Pole.Features.Events.Abstractions;
using Pole.Features.RedisServices.Domain;
using Pole.Features.RedisServices.Events;

namespace Pole.Features.RedisServices.Application.Common;

internal static class RedisServicesEventExtensions
{
    extension(Redis redis)
    {
        internal RedisServiceCreatedEvent ToCreateEvent()
            => new(
                redis.Id,
                redis.Name,
                redis.ResourceGroupId,
                redis.Spec.ToSnapshot(),
                redis.Status,
                redis.Tags)
            {
            EventCore = EventCore.Create(redis.TenantId)
            };

        internal RedisServiceDeletedEvent ToDeleteEvent()
            => new(
                redis.Id,
                redis.Name,
                redis.ResourceGroupId,
                redis.Spec.ToSnapshot(),
                redis.Tags)
            {
                EventCore = EventCore.Create(redis.TenantId)
            };
    }

    extension(RedisServiceCreatedEvent createEvent)
    {
        internal Redis ToRedis()
            => RedisFactory.Create(
                createEvent.EventCore.TenantId,
                createEvent.Name,
                createEvent.ResourceGroupId,
                createEvent.Spec.ToDomain(),
                RedisFactory.DefaultCreateOptions with
                {
                    Id = createEvent.RedisId,
                    Status = createEvent.Status,
                    Tags = createEvent.Tags
                });
    }

    extension(RedisSpec spec)
    {
        internal RedisServiceSpecSnapshot ToSnapshot()
            => new(
                spec.KubernetesNamespace,
                new RedisServiceStorageSnapshot(spec.Storage.SizeGb, spec.Storage.StorageClassName),
                spec.Resources);
    }

    extension(RedisServiceSpecSnapshot spec)
    {
        internal RedisSpec ToDomain()
            => new(
                spec.KubernetesNamespace,
                new Storage(spec.Storage.SizeGb, spec.Storage.StorageClassName),
                spec.Resources);
    }
}

