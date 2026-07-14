using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Contracts;

public interface ISourceBuildReferenceLookup
{
    Task<Result> EnsureSourceRepositoryCanBeDeletedAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default);

    Task<Result> EnsureWebServiceCanBeDeletedAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default);
}
