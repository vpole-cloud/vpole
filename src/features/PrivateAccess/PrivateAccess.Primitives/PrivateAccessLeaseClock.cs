// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Cryptography;

namespace Pole.Features.PrivateAccess.Primitives;

/// <summary>
///     A reply authorizes at most ten seconds from challenge creation, never from receipt.
///     Delayed replies cannot revive an expired lease or add their network delay to it.
/// </summary>
public sealed class PrivateAccessLeaseClock(
    TimeProvider time
)
{
    private readonly object sync = new();
    private long? acceptedStarted;
    private bool expired;
    private byte[]? pendingChallenge;
    private long pendingStarted;
    public static TimeSpan MaximumLease { get; } = TimeSpan.FromSeconds(10);
    public static TimeSpan RenewalInterval { get; } = TimeSpan.FromSeconds(3);

    public TimeSpan Remaining
    {
        get
        {
            lock (sync)
            {
                if (IsExpired())
                    return TimeSpan.Zero;
                var start = acceptedStarted ?? (pendingChallenge is not null
                    ? pendingStarted
                    : throw new InvalidOperationException("No lease challenge has started."));
                return MaximumLease - time.GetElapsedTime(start);
            }
        }
    }

    public byte[] Challenge()
    {
        lock (sync)
        {
            ThrowIfExpired();
            if (pendingChallenge is not null)
                throw new InvalidOperationException("A lease challenge is already pending.");
            pendingChallenge = RandomNumberGenerator.GetBytes(PrivateAccessFrames.ChallengeLength);
            pendingStarted = time.GetTimestamp();
            return (byte[])pendingChallenge.Clone();
        }
    }

    public bool Accept(ReadOnlySpan<byte> response)
    {
        lock (sync)
        {
            if (IsExpired() || pendingChallenge is null || time.GetElapsedTime(pendingStarted) >= MaximumLease ||
                !CryptographicOperations.FixedTimeEquals(pendingChallenge, response))
                return false;
            acceptedStarted = pendingStarted;
            pendingChallenge = null;
            return true;
        }
    }

    private bool IsExpired()
    {
        var start = acceptedStarted ?? (pendingChallenge is null ? null : pendingStarted);
        if (start is { } timestamp && time.GetElapsedTime(timestamp) >= MaximumLease)
            expired = true;
        return expired;
    }

    private void ThrowIfExpired()
    {
        if (IsExpired())
            throw new InvalidOperationException("An expired lease cannot be renewed.");
    }
}
