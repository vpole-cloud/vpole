// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel.DataAnnotations;

namespace Pole.Platform.Hosting;

public sealed class HangfireSchedulingOptions
{
    public const string SectionName = "Hangfire";

    [Range(1, 60)] public int SchedulePollingIntervalSeconds { get; init; } = 5;
}
