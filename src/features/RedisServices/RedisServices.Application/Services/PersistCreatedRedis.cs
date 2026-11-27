// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Mongo;
using Pole.Features.RedisServices.Domain;

namespace Pole.Features.RedisServices.Application.Services;

public sealed record RedisServiceNameAlreadyExists(
    string Name
);

public sealed record RedisServicePersisted;

public union PersistCreatedRedisOutcome(RedisServicePersisted, RedisServiceNameAlreadyExists);

public delegate Task<PersistCreatedRedisOutcome> PersistCreatedRedis(
    IClientSessionHandle session,
    Redis redis,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedRedis(
    IMongoCollection<Redis> redisServices,
    StoreOutboxWrites storeOutboxWrites
)
{
    public async Task<PersistCreatedRedisOutcome> InvokeAsync(
        IClientSessionHandle session,
        Redis redis,
        CancellationToken cancellationToken = default)
    {
        RedisPersistenceGuards.EnsureTransaction(session, "Redis creation");
        RedisPersistenceGuards.EnsureCreated(redis);

        try
        {
            await redisServices.InsertOneAsync(session, redis, cancellationToken: cancellationToken);
        }
        catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey &&
                                                    exception.WriteError.Message.Contains(
                                                        RedisIndexes.TenantName,
                                                        StringComparison.Ordinal))
        {
            return new RedisServiceNameAlreadyExists(redis.Name);
        }

        await storeOutboxWrites(session, [OutboxWrite.Create(redis.ToCreateEvent())], cancellationToken);
        return new RedisServicePersisted();
    }
}
