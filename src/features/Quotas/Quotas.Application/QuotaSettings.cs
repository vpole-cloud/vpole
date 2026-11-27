// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel.DataAnnotations;

namespace Pole.Features.Quotas.Application;

public sealed class QuotaSettings
{
    public const string SectionName = "Quotas";

    [Range(1, int.MaxValue)] public int ActiveReservationMinutes { get; set; } = 5;

    [Range(0, int.MaxValue)] public int FinalizedReservationHistoryDays { get; set; } = 7;

    internal TimeSpan ActiveReservationLifetime => TimeSpan.FromMinutes(ActiveReservationMinutes);
    internal TimeSpan FinalizedReservationHistory => TimeSpan.FromDays(FinalizedReservationHistoryDays);
}
