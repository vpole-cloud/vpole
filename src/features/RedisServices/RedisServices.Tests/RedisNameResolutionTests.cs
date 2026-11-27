// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceConnections.Contracts;
using Xunit;

namespace Pole.Features.RedisServices.Tests;

public sealed class RedisNameResolutionTests
{
    [Fact]
    public void Resolves_one_active_tenant_resource_by_id()
    {
        var redis = Create("redis-1");

        var result = ResourceConnectionNameResolution.ResolveUniqueResourceId(
            [redis],
            candidate => candidate.Lifecycle is not DeletingRedisLifecycle,
            candidate => candidate.Id);

        Assert.Equal("redis-1", result);
    }

    [Fact]
    public void Duplicate_tenant_names_fail_closed_instead_of_selecting_the_first_resource()
    {
        var matches = new[] { Create("redis-1"), Create("redis-2") };

        var result = ResourceConnectionNameResolution.ResolveUniqueResourceId(
            matches,
            candidate => candidate.Lifecycle is not DeletingRedisLifecycle,
            candidate => candidate.Id);

        Assert.Null(result);
    }

    [Fact]
    public void Deleting_resource_name_is_not_resolved()
    {
        var redis = Create("redis-1") with { Lifecycle = new DeletingRedisLifecycle(DateTime.UtcNow) };

        var result = ResourceConnectionNameResolution.ResolveUniqueResourceId(
            [redis],
            candidate => candidate.Lifecycle is not DeletingRedisLifecycle,
            candidate => candidate.Id);

        Assert.Null(result);
    }

    private static Redis Create(string id) => RedisFactory.Create(
        "tenant-1",
        "orders",
        "group-1",
        new RedisSpec(new Storage(1, "storage-profile-1")),
        RedisFactory.DefaultCreateOptions with { Id = id });
}
