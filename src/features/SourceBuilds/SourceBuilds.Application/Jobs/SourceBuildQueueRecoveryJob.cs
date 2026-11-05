using Hangfire;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Features.SourceBuilds.Application.Execution.Queueing;

namespace Pole.Features.SourceBuilds.Application.Jobs;

public sealed class SourceBuildQueueRecoveryJob(
    LoadStaleQueuedSourceBuildJobs loadStaleQueuedSourceBuildJobs,
    DispatchSourceBuildJob dispatchSourceBuildJob,
    TimeProvider timeProvider)
{
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task RecoverAsync()
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddSeconds(-30);
        var queued = await loadStaleQueuedSourceBuildJobs(cutoff, limit: 100);

        foreach (var buildJobId in queued)
            dispatchSourceBuildJob(buildJobId);
    }
}
