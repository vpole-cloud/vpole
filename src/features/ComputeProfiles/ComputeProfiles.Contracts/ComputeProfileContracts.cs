using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Contracts;

public enum ComputeArchitecture
{
    Any,
    Amd64,
    Arm64
}

public enum ComputeNodeAffinityOperator
{
    In,
    NotIn,
    Exists,
    DoesNotExist,
    GreaterThan,
    LessThan
}

public enum ComputeTolerationOperator
{
    Equal,
    Exists
}

public enum ComputeTaintEffect
{
    Any,
    NoSchedule,
    PreferNoSchedule,
    NoExecute
}

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

public interface IComputeProfileResolutionFence
{
    Task<Result> FenceAsync(
        ResolvedComputeProfile compute,
        string tenantId,
        ResourceTypeId resourceType,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}
