// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.PrivateAccess.Primitives;
using Xunit;

namespace Pole.Features.PrivateAccess.Tests;

public sealed class PrivateAccessLeaseClockTests
{
    [Fact]
    public void DelayedReply_DoesNotExtendTheDeadline()
    {
        var time = new ManualTime();
        var lease = new PrivateAccessLeaseClock(time);
        var challenge = lease.Challenge();
        time.Advance(TimeSpan.FromSeconds(7));
        Assert.True(lease.Accept(challenge));
        Assert.Equal(TimeSpan.FromSeconds(3), lease.Remaining);
    }

    [Fact]
    public void WrongAndReplayedResponses_DoNotRenew()
    {
        var time = new ManualTime();
        var lease = new PrivateAccessLeaseClock(time);
        var first = lease.Challenge();
        Assert.False(lease.Accept(new byte[32]));
        Assert.True(lease.Accept(first));
        time.Advance(TimeSpan.FromSeconds(3));
        var next = lease.Challenge();
        Assert.False(lease.Accept(first));
        Assert.Equal(TimeSpan.FromSeconds(7), lease.Remaining);
        Assert.True(lease.Accept(next));
    }

    [Fact]
    public void Expiry_IsTerminalEvenWithMatchingPendingResponse()
    {
        var time = new ManualTime();
        var lease = new PrivateAccessLeaseClock(time);
        Assert.True(lease.Accept(lease.Challenge()));
        time.Advance(TimeSpan.FromSeconds(9));
        var next = lease.Challenge();
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(lease.Accept(next));
        Assert.Equal(TimeSpan.Zero, lease.Remaining);
        Assert.Throws<InvalidOperationException>(() => lease.Challenge());
    }

    private sealed class ManualTime : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
}
