using Hangfire;
using Pole.Features.SourceBuilds.Application.Persistence.Logs;

namespace Pole.Features.SourceBuilds.Application.Jobs;

public sealed class SourceBuildLogRecoveryJob(RecoverSourceBuildLogs recover)
{
    [AutomaticRetry(Attempts = 0)]
    public Task RecoverAsync() => recover();
}
