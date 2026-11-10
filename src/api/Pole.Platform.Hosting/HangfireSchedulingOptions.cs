using System.ComponentModel.DataAnnotations;

namespace Pole.Platform.Hosting;

public sealed class HangfireSchedulingOptions
{
    public const string SectionName = "Hangfire";

    [Range(1, 60)] public int SchedulePollingIntervalSeconds { get; init; } = 5;
}
