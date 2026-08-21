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

public delegate Task<Result<
    ResolvedComputeProfile,
    ComputeProfileResolutionError>> ResolveComputeProfile(
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
    DefaultComputeProfileNotConfigured,
    ComputeProfileNotFound,
    ComputeProfileUnavailable);

public delegate Task<Result<
    IReadOnlyList<ResolvedComputeProfile>,
    ComputeProfileResolutionError>> ResolveComputeProfiles(
    ComputeProfileBatchResolutionRequest request,
    CancellationToken cancellationToken = default);

public delegate Task<Result<ResolvedComputeProfile>> ResolveComputeProfileLegacy(
    ComputeProfileResolutionRequest request,
    CancellationToken cancellationToken = default);

public delegate Task<Result<IReadOnlyList<ResolvedComputeProfile>>>
    ResolveComputeProfilesLegacy(
    ComputeProfileBatchResolutionRequest request,
    CancellationToken cancellationToken = default);

public sealed record ComputeProfileResolutionFenceRequest(
    string ComputeProfileId,
    long ComputeProfileRevision,
    string DeploymentTargetId,
    string TenantId,
    ResourceTypeId ResourceType);

public sealed record ComputeProfileResolutionFenceRejected(
    IReadOnlyList<string> ComputeProfileIds);

/// <summary>
/// Fences all resolved profiles inside the caller's MongoDB transaction.
/// </summary>
public delegate Task<Result<Unit, ComputeProfileResolutionFenceRejected>>
    FenceResolvedComputeProfiles(
        IReadOnlyList<ComputeProfileResolutionFenceRequest> requests,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
