namespace Pole.Features.SourceBuilds.Application.Logs;

public delegate Task<SourceBuildLogWriteOutcome> RenewSourceBuildLog(
    SourceBuildLogIdentity identity,
    CancellationToken cancellationToken = default);

internal sealed class MongoRenewSourceBuildLog(
    SourceBuildLogPersistence persistence
)
{
    public Task<SourceBuildLogWriteOutcome> InvokeAsync(
        SourceBuildLogIdentity identity,
        CancellationToken cancellationToken = default) => persistence.RenewAsync(identity, cancellationToken);
}
