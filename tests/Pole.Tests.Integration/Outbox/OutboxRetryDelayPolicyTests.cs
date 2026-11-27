// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Outbox.Application;
using Xunit;

namespace Pole.Tests.Integration.Outbox;

public sealed class OutboxRetryDelayPolicyTests
{
    private readonly OutboxRetryDelayPolicy _policy = new(
        new OutboxSettings
        {
            InitialRetryDelay = TimeSpan.FromSeconds(15),
            MaximumRetryDelay = TimeSpan.FromMinutes(5),
        });

    [Fact]
    public void Calculate_IsDeterministicAndGrowsExponentially()
    {
        var first = _policy.Calculate("507f1f77bcf86cd799439011", 1);
        var repeated = _policy.Calculate("507f1f77bcf86cd799439011", 1);
        var second = _policy.Calculate("507f1f77bcf86cd799439011", 2);

        Assert.Equal(first, repeated);
        Assert.InRange(first, TimeSpan.FromSeconds(13.5), TimeSpan.FromSeconds(16.5));
        Assert.True(second > first);
    }

    [Fact]
    public void Calculate_CapsLargeAttemptsAtMaximumDelay()
    {
        var delay = _policy.Calculate("507f1f77bcf86cd799439011", 100);

        Assert.InRange(delay, TimeSpan.FromMinutes(4.5), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Calculate_RequiresAFirstOrLaterAttempt()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _policy.Calculate("507f1f77bcf86cd799439011", 0));
    }
}
