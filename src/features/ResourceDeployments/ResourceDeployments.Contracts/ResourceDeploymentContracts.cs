using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record PlacementRequest(
    string? IsolationScopeId,
    string? DeploymentTargetId);

public sealed record ResourceDeploymentEndpointDto(
    string Kind,
    string Host,
    int Port,
    string Scheme)
{
    public static ResourceDeploymentEndpointDto Ingress(
        string host,
        int port = 443,
        string scheme = "https") =>
        new("Ingress", host, port, scheme);

    public static ResourceDeploymentEndpointDto Redis(
        string host,
        int port = 6379) =>
        new("Redis", host, port, "redis");

    public static ResourceDeploymentEndpointDto PostgreSql(
        string host,
        int port = 5432) =>
        new("PostgreSql", host, port, "postgresql");
}

public sealed record ComputeProfileReference(
    string ComputeProfileId,
    long ComputeProfileRevision);

public sealed record ComputeResourceValuesDto(
    long CpuMillis,
    long MemoryMi);

public sealed record ComputeExtendedResourceDto(
    string Name,
    long Quantity);

public sealed record ComputeConfigurationSummaryDto(
    ComputeProfileReference Profile,
    string DisplayName,
    ComputeResourceValuesDto Requests,
    ComputeResourceValuesDto Limits,
    ImmutableList<ComputeExtendedResourceDto> ExtendedResources);

public sealed record CurrentResourceDeploymentComputeDto(
    string DeploymentTargetId,
    ComputeConfigurationSummaryDto Compute);

public sealed record PlacementSummaryDto(
    string DeploymentId,
    string IsolationScopeId,
    string RuntimeCellId,
    string DeploymentTargetId,
    long DesiredGeneration,
    long ObservedGeneration,
    string Status,
    string? FailureCode,
    ComputeConfigurationSummaryDto Compute,
    ImmutableList<ResourceDeploymentEndpointDto> Endpoints);

public sealed record ResourceDeploymentDto(
    string Id,
    string ResourceType,
    string ResourceId,
    string ResourceGroupId,
    PlacementSummaryDto Placement,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public interface IResourceDeploymentReferenceLookup
{
    Task<Result> EnsureNoUnfinishedForResourceGroupAsync(
        string tenantId,
        string resourceGroupId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result> EnsureNoUnfinishedForIsolationScopeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public interface IResourceDeploymentComputeProfileReferenceGuard
{
    Task<Result> EnsureUnreferencedAsync(
        string computeProfileId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public interface IResourceDeploymentQueryService
{
    Task<PlacementSummaryDto?> GetAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, PlacementSummaryDto>> GetManyAsync(
        string tenantId,
        ResourceTypeId resourceType,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default);
}

public interface IResourceDeploymentComputeLookup
{
    Task<Result<CurrentResourceDeploymentComputeDto>> GetCurrentAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        CancellationToken cancellationToken = default);
}
