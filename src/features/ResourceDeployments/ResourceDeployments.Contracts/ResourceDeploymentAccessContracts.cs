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
    string Status,
    string DesiredOperation);

/// <summary>Reads and fences the deployment in the caller's Mongo transaction.</summary>
public delegate Task<ResourceDeploymentAccessPlacement?> LoadExactResourceDeploymentAccessPlacement(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    string deploymentId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
