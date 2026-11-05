using System.Collections.Immutable;

namespace Pole.ArchitectureAnalyzers;

// Remove these exact allowances when SourceBuilds leaves its legacy Persistence/Jobs layout.
public static class MongoApplicationLegacyPaths
{
    private const string SourceBuildJobsDirectory =
        "src/features/SourceBuilds/SourceBuilds.Application/Persistence/Jobs/";

    public static readonly ImmutableHashSet<string> Files = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        SourceBuildJobsDirectory + "ClaimQueuedSourceBuildJob.cs",
        SourceBuildJobsDirectory + "CompleteSourceBuildJobOnDeployment.cs",
        SourceBuildJobsDirectory + "LoadSourceBuildJob.cs",
        SourceBuildJobsDirectory + "LoadSourceBuildJobPage.cs",
        SourceBuildJobsDirectory + "LoadStaleQueuedSourceBuildJobs.cs",
        SourceBuildJobsDirectory + "QueueSourceBuildJob.cs",
        SourceBuildJobsDirectory + "SourceBuildExecutionLease.cs",
        SourceBuildJobsDirectory + "SourceBuildPromotion.cs",
        SourceBuildJobsDirectory + "TryPersistSourceBuildJob.cs");

    internal static bool Contains(string normalizedPath) =>
        Files.Any(file => normalizedPath == file || normalizedPath.EndsWith("/" + file, StringComparison.Ordinal));
}
