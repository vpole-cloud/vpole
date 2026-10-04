using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Logs;

public delegate Task<SourceBuildLogWriteOutcome> SealSourceBuildLog(
    SourceBuildLogIdentity identity,
    bool unavailable,
    CancellationToken cancellationToken = default);

internal sealed class MongoSealSourceBuildLog(SourceBuildLogPersistence persistence)
{
    public Task<SourceBuildLogWriteOutcome> InvokeAsync(
        SourceBuildLogIdentity identity,
        bool unavailable,
        CancellationToken cancellationToken = default) =>
        persistence.SealAsync(identity, unavailable, cancellationToken);
}
