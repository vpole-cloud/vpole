using System.Collections.Immutable;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record ResourceDeploymentComputeResourceValues(
    long CpuMillis,
    long MemoryMi
);

public sealed record ResourceDeploymentExtendedResource(
    string Name,
    long Quantity
);

public sealed record ResourceDeploymentNodeSelector(
    string Key,
    string Value
);

public sealed record ResourceDeploymentNodeAffinityExpression(
    string Key,
    ComputeNodeAffinityOperator Operator,
    ImmutableList<string> Values
);

public sealed record ResourceDeploymentNodeAffinityTerm(
    ImmutableList<ResourceDeploymentNodeAffinityExpression> Expressions
);

public sealed record ResourceDeploymentPreferredNodeAffinityTerm(
    int Weight,
    ResourceDeploymentNodeAffinityTerm Term
);

public sealed record ResourceDeploymentToleration(
    string? Key,
    ComputeTolerationOperator Operator,
    string? Value,
    ComputeTaintEffect Effect,
    long? TolerationSeconds
);

/// <summary>
///     ResourceDeployments-owned representation of an immutable compute resolution.
///     Callers map provider contracts into this snapshot before crossing the feature boundary.
/// </summary>
public sealed record ResourceDeploymentComputeConfiguration(
    string ComputeProfileId,
    long ComputeProfileRevision,
    string DeploymentTargetId,
    string DisplayName,
    ResourceDeploymentComputeResourceValues Requests,
    ResourceDeploymentComputeResourceValues Limits,
    ComputeArchitecture Architecture,
    ImmutableList<ResourceDeploymentExtendedResource> ExtendedResources,
    ImmutableList<ResourceDeploymentNodeSelector> NodeSelector,
    ImmutableList<ResourceDeploymentNodeAffinityTerm> RequiredNodeAffinity,
    ImmutableList<ResourceDeploymentPreferredNodeAffinityTerm> PreferredNodeAffinity,
    ImmutableList<ResourceDeploymentToleration> Tolerations,
    string? RuntimeClassName
);
