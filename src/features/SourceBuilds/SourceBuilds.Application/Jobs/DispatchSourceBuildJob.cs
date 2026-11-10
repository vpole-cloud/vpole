using Hangfire;

namespace Pole.Features.SourceBuilds.Application.Jobs;

public delegate string DispatchSourceBuildJob(string buildJobId);

public sealed class HangfireSourceBuildJobDispatcher(
    IBackgroundJobClient backgroundJobs
)
{
    public string Invoke(string buildJobId) =>
        backgroundJobs.Enqueue<SourceBuildJobExecutor>(executor => executor.ExecuteAsync(buildJobId));
}
