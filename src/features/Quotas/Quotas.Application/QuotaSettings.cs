using System.ComponentModel.DataAnnotations;

namespace Pole.Features.Quotas.Application;

public sealed class QuotaSettings
{
    public const string SectionName = "Quotas";

    [Range(1, int.MaxValue)]
    public int ActiveReservationMinutes { get; set; } = 5;

    [Range(0, int.MaxValue)]
    public int FinalizedReservationHistoryDays { get; set; } = 7;

    internal TimeSpan ActiveReservationLifetime => TimeSpan.FromMinutes(ActiveReservationMinutes);
    internal TimeSpan FinalizedReservationHistory => TimeSpan.FromDays(FinalizedReservationHistoryDays);
}
