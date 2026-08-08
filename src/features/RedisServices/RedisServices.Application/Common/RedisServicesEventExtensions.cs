using System.Collections.Immutable;
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

        internal RedisServiceUpdatedEvent ToUpdateEvent(Redis replacement)
        {
            ArgumentNullException.ThrowIfNull(replacement);
            return new RedisServiceUpdatedEvent(
                redis.Id,
                redis.Name,
                redis.ResourceGroupId,
                new RedisServiceUpdatedEventChanges(
                    UpdateLog<RedisServiceSpecSnapshot>.Of(
                        redis.Spec.ToSnapshot(),
                        replacement.Spec.ToSnapshot()),
                    UpdateLog<ImmutableHashSet<string>>.Of(
                        redis.Tags,
                        replacement.Tags)))
            {
                EventCore = EventCore.Create(redis.TenantId)
            };
        }
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
                    Tags = createEvent.Tags
                });
    }

    extension(RedisSpec spec)
    {
        internal RedisServiceSpecSnapshot ToSnapshot()
            => new(
                new RedisServiceStorageSnapshot(
                    spec.Storage.SizeGb,
                    spec.Storage.StorageProfileId));
    }

    extension(RedisServiceSpecSnapshot spec)
    {
        internal RedisSpec ToDomain()
            => new(
                new Storage(
                    spec.Storage.SizeGb,
                    spec.Storage.StorageProfileId));
    }
}
