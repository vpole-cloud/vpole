using System.ComponentModel.DataAnnotations;

namespace Pole.Features.GitOps.Application;

public sealed class GitOpsWorkerOptions
{
    public const string SectionName = "GitOpsWorker";

    [Range(1, 60)] public int ScanIntervalMinutes { get; init; } = 1;

    [Range(1, 1000)] public int BatchSize { get; init; } = 100;

    [Range(30, 3600)] public int LeaseSeconds { get; init; } = 120;

    [Range(1, 300)] public int HeartbeatSeconds { get; init; } = 20;

    [Range(1, 300)] public int CleanupCheckSeconds { get; init; } = 5;

    [Range(1, 5)] public int MaximumConsecutiveFailures { get; init; } = 5;

    public TimeSpan RetryDelay(int failures) => TimeSpan.FromMinutes(failures switch
    {
        <= 1 => 1,
        2 => 2,
        3 => 4,
        _ => 5
    });
}
