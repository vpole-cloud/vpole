using System.Collections.Immutable;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

/// <summary>Closed provider-owned ingress paths.</summary>
public enum ResourceIngressPolicyPurpose
{
    WebServicePublic,
    WebServiceHttp01,
    FunctionGateway,
    PostgresOperator,
    PostgresReplication
}

/// <summary>
/// Both revisions advance independently. An empty port list leaves a deny tombstone.
/// The writer resolves the purpose's platform source from the current target configuration.
/// </summary>
public sealed record ResourceIngressPolicyRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string RuntimeCellId,
    string DeploymentTargetId,
    string KubernetesNamespace,
    ResourceIngressPolicyPurpose Purpose,
    long DeploymentDesiredGeneration,
    long TargetWorkloadConfigurationRevision,
    ImmutableList<int> AllowedTcpPorts);

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
