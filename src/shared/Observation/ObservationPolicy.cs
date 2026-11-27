// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Observation;

/// <summary>One timing contract for demand-driven snapshots; independent of resource/provider semantics.</summary>
public static class ObservationPolicy
{
    public const int ConcurrentReads = 4;
    public static readonly TimeSpan CoordinationInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan CoalesceInterval = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ConfirmationInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan DemandDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan IdleRetention = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);
}
