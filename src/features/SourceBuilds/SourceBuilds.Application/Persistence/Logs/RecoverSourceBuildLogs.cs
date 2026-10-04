using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Logs;

public delegate Task RecoverSourceBuildLogs(
    CancellationToken cancellationToken = default);

internal sealed class MongoRecoverSourceBuildLogs(SourceBuildLogPersistence persistence)
{
    public Task InvokeAsync(
        CancellationToken cancellationToken = default) =>
        persistence.RecoverAsync(cancellationToken);
}
