// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Pole.Features.Events.Abstractions;
using Xunit;

namespace Pole.Tests.Integration.Events;

public sealed class UpdateLogTests
{
    [Fact]
    public void Of_ReturnsEqual_ForEquivalentSetsWithDifferentConstructionOrder()
    {
        var previous = ImmutableHashSet.Create("cache", "prod");
        var next = ImmutableHashSet.Create("prod", "cache");

        var updateLog = UpdateLog<ImmutableHashSet<string>>.Of(previous, next);

        Assert.True(updateLog.AreEqual);
    }
}
