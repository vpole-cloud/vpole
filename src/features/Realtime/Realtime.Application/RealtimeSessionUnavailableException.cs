// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Realtime.Application;

/// <summary>Signals a missing, expired or explicitly revoked realtime session at a storage boundary.</summary>
public sealed class RealtimeSessionUnavailableException : Exception
{
    public RealtimeSessionUnavailableException() : base("The realtime session is unavailable.")
    {
    }
}
