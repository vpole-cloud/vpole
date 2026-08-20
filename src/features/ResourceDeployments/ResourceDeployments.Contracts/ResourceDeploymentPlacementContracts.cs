using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record ResourceDeploymentIngressConfiguration(
    string IngressClassName,
    string ClusterIssuerName,
    string? PublicBaseDomain,
    ImmutableList<string> Hostnames)
{
    public static ResourceDeploymentIngressConfiguration Create(
        string ingressClassName,
        string clusterIssuerName,
        string? publicBaseDomain,
        IEnumerable<string>? hostnames) =>
        new(
            ingressClassName,
            clusterIssuerName,
            publicBaseDomain,
            (hostnames ?? []).ToImmutableList());
}

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
    ResourcePlacementCapability PlacementCapability,
    int FixedReplicas = 1,
    long ResourceRevision = 1);

public sealed record CreateResourceDeploymentSetRequest(
    IReadOnlyList<CreateResourceDeploymentRequest> Deployments);

public sealed record InvalidResourceDeploymentSet(
    IReadOnlyDictionary<string, string[]> Errors);

public sealed record ResourceDeploymentTargetConfigurationChanged(
    IReadOnlyList<string> DeploymentTargetIds);

public sealed record ResourceDeploymentComputeConfigurationChanged(
    IReadOnlyList<string> ComputeProfileIds);

public sealed record ResourceDeploymentStorageConfigurationChanged(
    string StorageProfileId);

public sealed record ResourcePlacementUnavailable(string ResourceGroupId);

public sealed record ResourcePlacementResourceGroupNotFound(string ResourceGroupId);

public sealed record ResourcePlacementDeleting(string DeploymentTargetId);

public union CreateResourceDeploymentSetError(
    InvalidResourceDeploymentSet,
    ResourceDeploymentTargetConfigurationChanged,
    ResourceDeploymentComputeConfigurationChanged,
    ResourceDeploymentStorageConfigurationChanged,
    ResourcePlacementResourceGroupNotFound,
    ResourcePlacementUnavailable,
    ResourcePlacementDeleting);

public delegate Task<Result<ResolvedResourceDeploymentTarget>> ResolveResourceDeploymentTarget(
    string? deploymentTargetId,
    bool requiresIngress,
    CancellationToken cancellationToken = default);

public delegate Task<Result<PlacementSummaryDto>> CreateResourceDeployment(
    CreateResourceDeploymentRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<Result<IReadOnlyList<PlacementSummaryDto>>> CreateResourceDeploymentSet(
    CreateResourceDeploymentSetRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<Result<
    IReadOnlyList<PlacementSummaryDto>,
    CreateResourceDeploymentSetError>> CreateResourceDeploymentSetOutcome(
    CreateResourceDeploymentSetRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
