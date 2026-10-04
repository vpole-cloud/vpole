using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Common;

public sealed class SourceBuildLogOptions
{
    public const string SectionName = "SourceBuildLogs";
    public int MaxBytes { get; init; } = 1024 * 1024;
    public int MaxEntries { get; init; } = 2048;
    public int MaxEntryBytes { get; init; } = 4096;
    public int RetentionDays { get; init; } = 7;
    public SourceBuildLogLimits Limits => new(MaxBytes, MaxEntries, MaxEntryBytes, RetentionDays);
    public bool Valid => MaxBytes is >= 4096 and <= 4 * 1024 * 1024 && MaxEntries is >= 16 and <= 4096 &&
        MaxEntryBytes is >= 256 and <= 8192 && MaxEntryBytes <= MaxBytes && RetentionDays is >= 1 and <= 30;
}
