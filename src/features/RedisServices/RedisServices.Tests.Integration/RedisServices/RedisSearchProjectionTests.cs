// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Pole.Features.Events.Abstractions;
using Pole.Features.RedisServices.Application.Search;
using Pole.Features.RedisServices.Events;
using Pole.Features.RedisServices.Tests.Integration.Infrastructure;
using Pole.Features.Search.Domain;
using Xunit;

namespace Pole.Features.RedisServices.Tests.Integration.RedisServices;

public sealed class RedisSearchProjectionTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task CreatedConsumer_ProjectsTheRedisSourceType()
    {
        var sourceId = ObjectId.GenerateNewId().ToString();
        var resourceGroupId = ObjectId.GenerateNewId().ToString();
        var createProjection = Services.GetRequiredService<CreateSearchProjection>();

        await new AddRedisServiceToSearchOnRedisServiceCreatedConsumer(createProjection).Handle(
            new RedisServiceCreatedEvent(
                sourceId,
                "cache",
                resourceGroupId,
                new RedisServiceSpecSnapshot(new RedisServiceStorageSnapshot(5, RedisTestData.DefaultStorageProfileId)),
                ImmutableHashSet<string>.Empty,
                1)
            {
                EventCore = EventCore.Create(RedisTestData.TenantId),
            },
            CancellationToken.None);

        var source = await Fixture.Database
            .GetCollection<SearchSourceDocument>("SearchSourceDocuments")
            .Find(item => item.TenantId == RedisTestData.TenantId && item.SourceId == sourceId)
            .SingleAsync();

        Assert.Equal("RedisService", source.SourceApiName);
        Assert.Equal("pole/redis", source.ResourceTypeId);
        Assert.Equal("cache", source.Title);
    }
}
