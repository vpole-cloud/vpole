using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Logs;

public delegate Task<SourceBuildLogWriteOutcome> StartSourceBuildLog(
    SourceBuildLog log,
    CancellationToken cancellationToken = default);

internal sealed class MongoStartSourceBuildLog(SourceBuildLogPersistence persistence)
{
    public Task<SourceBuildLogWriteOutcome> InvokeAsync(
        SourceBuildLog log,
        CancellationToken cancellationToken = default) =>
        persistence.StartAsync(log, cancellationToken);
}
