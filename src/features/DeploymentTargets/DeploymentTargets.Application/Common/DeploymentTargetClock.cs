// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.DeploymentTargets.Application.Common;

internal static class DeploymentTargetClock
{
    public static DateTime UtcNowAfter(TimeProvider timeProvider, DateTime? floor)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return floor is { } value && value > now ? value : now;
    }
}
