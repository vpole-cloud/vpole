namespace Pole.Features.SourceBuilds.Contracts;

public interface ISourceBuildReferenceLookup
{
    Task<bool> HasConfigurationForSourceRepositoryAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default);

    Task<bool> HasConfigurationForWebServiceAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default);
}
