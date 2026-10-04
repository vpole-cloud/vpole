using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Logs;

public delegate Task<SourceBuildLogWriteOutcome> AppendSourceBuildLog(
    SourceBuildLogIdentity identity,
    long sequence,
    SourceBuildLogStream stream,
    string sanitizedText,
    CancellationToken cancellationToken = default);

internal sealed class MongoAppendSourceBuildLog(SourceBuildLogPersistence persistence)
{
    public Task<SourceBuildLogWriteOutcome> InvokeAsync(
        SourceBuildLogIdentity identity,
        long sequence,
        SourceBuildLogStream stream,
        string sanitizedText,
        CancellationToken cancellationToken = default) =>
        persistence.AppendAsync(identity, sequence, stream, sanitizedText, cancellationToken);
}
