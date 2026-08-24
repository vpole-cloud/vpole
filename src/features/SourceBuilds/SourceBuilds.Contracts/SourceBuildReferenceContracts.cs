using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Contracts;

public sealed record SourceBuildWebServiceConfigurationExists(string WebServiceId);

public sealed record SourceBuildSourceRepositoryConfigurationExists(string SourceRepositoryId);

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

/// <summary>
/// Guards a source-repository deletion against source-build configuration that
/// still references it. The source-build feature owns this invariant.
/// </summary>
public delegate Task<Result<Unit, SourceBuildSourceRepositoryConfigurationExists>>
    EnsureNoSourceRepositoryBuildConfiguration(
        string tenantId,
        string sourceRepositoryId,
        CancellationToken cancellationToken = default);
