using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Logs;

public delegate Task<SourceBuildLog?> LoadSourceBuildLog(
    string tenantId,
    string webServiceId,
    string jobId,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadSourceBuildLog(
    SourceBuildLogPersistence persistence
)
{
    public Task<SourceBuildLog?> InvokeAsync(
        string tenantId,
        string webServiceId,
        string jobId,
        CancellationToken cancellationToken = default) =>
        persistence.LoadAsync(tenantId, webServiceId, jobId, cancellationToken);
}
