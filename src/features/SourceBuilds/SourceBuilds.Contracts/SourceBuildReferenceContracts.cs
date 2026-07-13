using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Contracts;

public interface ISourceBuildReferenceLookup
{
    Task<Result<bool>> HasConfigurationForSourceRepositoryAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default);

    Task<Result<bool>> HasConfigurationForWebServiceAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default);
}
