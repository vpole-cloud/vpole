using System.Collections.Immutable;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

/// <summary>A provider-owned ingress path resolved by resource type and purpose.</summary>
public sealed record ResourceIngressPolicyDefinition(
    ResourceTypeId ResourceType,
    string Purpose,
    bool RequiresIngressPlacement,
    Func<ImmutableList<NetworkPort>, bool> AllowsPorts,
    Func<ResourceIngressPolicyRequest, ImmutableDictionary<string, string>> DestinationPodLabels,
    Func<ResourceIngressPolicyRequest, ResourceIngressPolicyTargetSources, ResourceIngressPolicyNetworkSource?> ResolveSource);

public sealed record ResourceIngressPolicyTargetSources(
    IReadOnlyDictionary<string, ResourceIngressPolicyNetworkSource?> Sources)
{
    public ResourceIngressPolicyNetworkSource? Get(string name) =>
        Sources.TryGetValue(name, out var source) ? source : null;
}

public closed record ResourceIngressPolicyNetworkSource;

public sealed record ResourceIngressPolicyPodSource(
    string Namespace,
    ImmutableDictionary<string, string> PodLabels) : ResourceIngressPolicyNetworkSource;

public sealed record ResourceIngressPolicyCidrSource(string Cidr) : ResourceIngressPolicyNetworkSource;

/// <summary>
/// Both revisions advance independently. An empty port list leaves a deny tombstone.
/// The registered provider definition resolves the source from the current target configuration.
/// </summary>
public sealed record ResourceIngressPolicyRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string RuntimeCellId,
    string DeploymentTargetId,
    string KubernetesNamespace,
    string Purpose,
    long DeploymentDesiredGeneration,
    long TargetWorkloadConfigurationRevision,
    ImmutableList<NetworkPort> AllowedTcpPorts);

public sealed record ResourceIngressPolicyApplied;

public sealed record ResourceIngressPolicyStaleRevision;

public sealed record ResourceIngressPolicyPlacementMismatch;

public sealed record ResourceIngressPolicyOwnershipConflict;

public sealed record ResourceIngressPolicyUnavailable;

public sealed record ResourceIngressPolicySourceUnavailable;

public union ReconcileResourceIngressPolicyOutcome(
    ResourceIngressPolicyApplied,
    ResourceIngressPolicyStaleRevision,
    ResourceIngressPolicyPlacementMismatch,
    ResourceIngressPolicyOwnershipConflict,
    ResourceIngressPolicyUnavailable,
    ResourceIngressPolicySourceUnavailable);

public delegate Task<ReconcileResourceIngressPolicyOutcome> ReconcileResourceIngressPolicy(
    ResourceIngressPolicyRequest request,
    CancellationToken cancellationToken = default);
