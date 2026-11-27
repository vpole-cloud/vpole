// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Events.Abstractions;
using Pole.Features.RedisServices.Domain;
using Xunit;

namespace Pole.Features.RedisServices.Tests;

public sealed class RedisUpdateLogTests
{
    [Fact]
    public void Of_ReturnsEqual_ForEquivalentSpecs()
    {
        var previous = new RedisSpec(new Storage(5, "507f1f77bcf86cd799439099"));
        var next = new RedisSpec(new Storage(5, "507f1f77bcf86cd799439099"));

        var updateLog = UpdateLog<RedisSpec>.Of(previous, next);

        Assert.True(updateLog.AreEqual);
    }
}
