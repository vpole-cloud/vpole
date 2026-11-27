// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.ResourceGroups.Application.Common;

internal static class RuntimeCellVerificationSchedule
{
    public static DateTime DueBefore(DateTime now) => now.AddMinutes(-5);
}
