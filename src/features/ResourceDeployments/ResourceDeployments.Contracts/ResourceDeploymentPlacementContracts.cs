using MongoDB.Driver;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record ResourceDeploymentIngressConfiguration(
    string IngressClassName,
    string ClusterIssuerName);

public sealed record ResourceDeploymentStorageConfiguration(
    string StorageProfileId,
    long StorageProfileRevision,
    string StorageClassName);

public sealed record ResolvedResourceDeploymentTarget(
    string DeploymentTargetId,
    long WorkloadConfigurationRevision,
    ResourceDeploymentIngressConfiguration? Ingress);

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
    ResourcePlacementCapability PlacementCapability);

public delegate Task<Result<ResolvedResourceDeploymentTarget>> ResolveResourceDeploymentTarget(
    string? deploymentTargetId,
    bool requiresIngress,
    CancellationToken cancellationToken = default);

public delegate Task<Result<PlacementSummaryDto>> CreateResourceDeployment(
    CreateResourceDeploymentRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
