using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.ComputeProfiles.Contracts;

public sealed record ComputeResourceValues(
    long CpuMillis,
    long MemoryMi);

public sealed record ComputeExtendedResourceValue(
    string Name,
    long Quantity);

public sealed record ComputeNodeSelectorValue(
    string Key,
    string Value);

public sealed record ComputeNodeAffinityExpressionValue(
    string Key,
    ComputeNodeAffinityOperator Operator,
    ImmutableList<string> Values);

public sealed record ComputeNodeAffinityTermValue(
    ImmutableList<ComputeNodeAffinityExpressionValue> Expressions);

public sealed record ComputePreferredNodeAffinityTermValue(
    int Weight,
    ComputeNodeAffinityTermValue Term);

public sealed record ComputeTolerationValue(
    string? Key,
    ComputeTolerationOperator Operator,
    string? Value,
    ComputeTaintEffect Effect,
    long? TolerationSeconds);

public sealed record ResolvedComputeProfile(
    string ComputeProfileId,
    long ComputeProfileRevision,
    string DeploymentTargetId,
    string DisplayName,
    ComputeResourceValues Requests,
    ComputeResourceValues Limits,
    ComputeArchitecture Architecture,
    ImmutableList<ComputeExtendedResourceValue> ExtendedResources,
    ImmutableList<ComputeNodeSelectorValue> NodeSelector,
    ImmutableList<ComputeNodeAffinityTermValue> RequiredNodeAffinity,
    ImmutableList<ComputePreferredNodeAffinityTermValue> PreferredNodeAffinity,
    ImmutableList<ComputeTolerationValue> Tolerations,
    string? RuntimeClassName);

public sealed record ComputeProfileResolutionRequest(
    string? ComputeProfileId,
    string DeploymentTargetId,
    string TenantId,
    ResourceTypeId ResourceType);

public delegate Task<Result<ResolvedComputeProfile>> ResolveComputeProfile(
    ComputeProfileResolutionRequest request,
    CancellationToken cancellationToken = default);

public sealed record ComputeProfileTargetResolutionRequest(
    string DeploymentTargetId,
    string? ComputeProfileId);

/// <summary>
/// Resolves the compute profile required by each selected deployment target.
/// Every target is resolved for one tenant and logical resource type.
/// </summary>
public sealed record ComputeProfileBatchResolutionRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    IReadOnlyList<ComputeProfileTargetResolutionRequest> Targets);

public sealed record InvalidComputeProfileResolutionRequest(
    IReadOnlyDictionary<string, string[]> Errors);

public sealed record DefaultComputeProfileNotConfigured;

public sealed record ComputeProfileNotFound(string ComputeProfileId);

public enum ComputeProfileUnavailableReason
{
    Disabled,
    ResourceTypeNotAllowed,
    TenantNotAllowed,
    DeploymentTargetNotConfigured
}

public sealed record ComputeProfileUnavailable(
    string ComputeProfileId,
    ComputeProfileUnavailableReason Reason);

public union ComputeProfileResolutionError(
    InvalidComputeProfileResolutionRequest,
    DefaultComputeProfileNotConfigured,
    ComputeProfileNotFound,
    ComputeProfileUnavailable);

public delegate Task<Result<IReadOnlyList<ResolvedComputeProfile>>> ResolveComputeProfiles(
    ComputeProfileBatchResolutionRequest request,
    CancellationToken cancellationToken = default);

public delegate Task<Result<
    IReadOnlyList<ResolvedComputeProfile>,
    ComputeProfileResolutionError>> ResolveComputeProfilesOutcome(
    ComputeProfileBatchResolutionRequest request,
    CancellationToken cancellationToken = default);

public sealed record ComputeProfileResolutionFenceRequest(
    string ComputeProfileId,
    long ComputeProfileRevision,
    string DeploymentTargetId,
    string TenantId,
    ResourceTypeId ResourceType);

public interface IComputeProfileResolutionFence
{
    Task<Result> FenceAsync(
        ComputeProfileResolutionFenceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fences all resolved profiles in the caller's MongoDB transaction.
    /// Implementations should batch the persistence work. The default preserves
    /// compatibility for feature-local fakes that only need the single fence.
    /// </summary>
    async Task<Result> FenceManyAsync(
        IReadOnlyList<ComputeProfileResolutionFenceRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        foreach (var request in requests)
        {
            var result = await FenceAsync(request, session, cancellationToken);
            if (result is not ISuccess)
                return result;
        }

        return Result.Success();
    }
}
