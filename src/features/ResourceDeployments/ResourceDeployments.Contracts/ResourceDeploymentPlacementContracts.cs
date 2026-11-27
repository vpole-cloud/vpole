// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record ResourceDeploymentIngressConfiguration(
    string IngressClassName,
    string ClusterIssuerName,
    string? PublicBaseDomain,
    ImmutableList<string> Hostnames
)
{
    public static ResourceDeploymentIngressConfiguration Create(
        string ingressClassName,
        string clusterIssuerName,
        string? publicBaseDomain,
        IEnumerable<string>? hostnames) => new(
        ingressClassName,
        clusterIssuerName,
        publicBaseDomain,
        (hostnames ?? []).ToImmutableList());
}

public sealed record ResourceDeploymentStorageConfiguration(
    string StorageProfileId,
    long StorageProfileRevision,
    string StorageClassName
);

public sealed record ResolvedResourceDeploymentTarget(
    string DeploymentTargetId,
    long WorkloadConfigurationRevision,
    ResourceDeploymentIngressConfiguration? Ingress
);

public sealed record CreateResourceDeploymentRequest(
    string DeploymentId,
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceGroupId,
    string? IsolationScopeId,
    ResolvedResourceDeploymentTarget Target,
    ResourceDeploymentComputeConfiguration Compute,
    ResourceDeploymentStorageConfiguration? Storage,
    ResourcePlacementCapability PlacementCapability,
    int FixedReplicas = 1,
    long ResourceRevision = 1,
    bool DeferredActivation = false,
    bool PinnedCompute = false,
    long? StoragePolicyRevision = null
);

public sealed record CreateResourceDeploymentSetRequest(
    IReadOnlyList<CreateResourceDeploymentRequest> Deployments
);

public sealed record ResourceDeploymentTargetConfigurationChanged(
    IReadOnlyList<string> DeploymentTargetIds
);

public sealed record ResourceDeploymentComputeConfigurationChanged(
    IReadOnlyList<string> ComputeProfileIds
);

public sealed record ResourceDeploymentStorageConfigurationChanged(
    string StorageProfileId
);

public sealed record ResourcePlacementUnavailable(
    string ResourceGroupId
);

public sealed record ResourcePlacementResourceGroupNotFound(
    string ResourceGroupId
);

public sealed record ResourcePlacementDeleting(
    string DeploymentTargetId
);

public union CreateResourceDeploymentError(
    ResourceDeploymentConnectionConflict,
    ResourceDeploymentTargetConfigurationChanged,
    ResourceDeploymentComputeConfigurationChanged,
    ResourceDeploymentStorageConfigurationChanged,
    ResourcePlacementResourceGroupNotFound,
    ResourcePlacementUnavailable,
    ResourcePlacementDeleting);

public union CreateResourceDeploymentOutcome(PlacementSummaryDto, CreateResourceDeploymentError);

public sealed record CreatedResourceDeployments(
    IReadOnlyList<PlacementSummaryDto> Placements
);

public union CreateResourceDeploymentSetOutcome(CreatedResourceDeployments, CreateResourceDeploymentError);

public delegate Task<CreateResourceDeploymentOutcome> CreateResourceDeployment(
    CreateResourceDeploymentRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<CreateResourceDeploymentSetOutcome> CreateResourceDeploymentSet(
    CreateResourceDeploymentSetRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
