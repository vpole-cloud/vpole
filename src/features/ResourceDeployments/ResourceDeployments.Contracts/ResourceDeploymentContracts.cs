using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MongoDB.Driver;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Contracts;

public readonly partial record struct ResourceTypeId
{
    public const int MaximumLength = 128;

    private ResourceTypeId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ResourceTypeId Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Resource type is required.", nameof(value));

        var normalized = value.Trim();
        if (normalized.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"Resource type must be {MaximumLength} characters or less.",
                nameof(value));
        }
        if (!ResourceTypePattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                "Resource type must contain lowercase path segments separated by '/'.",
                nameof(value));
        }

        return new ResourceTypeId(normalized);
    }

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?(?:/[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?)+$")]
    private static partial Regex ResourceTypePattern();
}

public enum ResourcePlacementCapability
{
    SingleTarget,
    MultiTarget
}

public sealed record PlacementRequest(
    string? IsolationScopeId,
    string? DeploymentTargetId);

public sealed record ResourceDeploymentEndpointDto(
    string Kind,
    string Host,
    int Port,
    string Scheme);

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
