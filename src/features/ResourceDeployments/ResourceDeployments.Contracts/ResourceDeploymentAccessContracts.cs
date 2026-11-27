// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

/// <summary>Exact server-owned placement identity for private access admission.</summary>
public sealed record ResourceDeploymentAccessPlacement(
    string TenantId,
    string ResourceType,
    string ResourceId,
    string ResourceGroupId,
    string DeploymentId,
    string DeploymentTargetId,
    string RuntimeCellId,
    string IsolationScopeId,
    string KubernetesNamespace,
    long DeploymentRevision,
    long DesiredGeneration,
    ResourceDeploymentStatus Status,
    ResourceDeploymentDesiredOperation DesiredOperation
);

/// <summary>Reads and fences the deployment in the caller's Mongo transaction.</summary>
public delegate Task<ResourceDeploymentAccessPlacement?> LoadExactResourceDeploymentAccessPlacement(
    LoadExactResourceDeploymentAccessPlacementRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public sealed record LoadExactResourceDeploymentAccessPlacementRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string DeploymentId
);
