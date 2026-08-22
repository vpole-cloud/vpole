using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Contracts;

public interface ISourceBuildReferenceLookup
{
    Task<Result> EnsureSourceRepositoryCanBeDeletedAsync(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default);

}

public sealed record SourceBuildWebServiceConfigurationExists(string WebServiceId);

/// <summary>
/// Guards a WebService deletion against source-build configuration that still
/// references it. The guard is provider-owned so consumers do not translate a
/// source-build invariant through generic application failures.
/// </summary>
public delegate Task<Result<Unit, SourceBuildWebServiceConfigurationExists>>
    EnsureNoWebServiceSourceBuildConfiguration(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default);
