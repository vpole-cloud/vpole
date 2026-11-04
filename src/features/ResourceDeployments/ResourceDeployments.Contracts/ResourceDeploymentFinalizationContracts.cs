using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public enum ResourceDeploymentFinalizationReadiness
{
    Missing,
    Waiting,
    Ready
}

public sealed record ResourceDeploymentFinalizationConflict(
    string ResourceId,
    string? ResourceDeploymentId = null);

public sealed record ResourceDeploymentsPurged;
public union PurgeResourceDeploymentsOutcome(
    ResourceDeploymentsPurged,
    ResourceDeploymentFinalizationConflict);

public delegate Task<ResourceDeploymentFinalizationReadiness>
    GetResourceDeploymentFinalizationReadiness(
    GetResourceDeploymentFinalizationReadinessRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<PurgeResourceDeploymentsOutcome>
    PurgeDeletedResourceDeployments(
    PurgeDeletedResourceDeploymentsRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<ResourceDeploymentFinalizationReadiness>
    GetExactResourceDeploymentFinalizationReadiness(
    GetExactResourceDeploymentFinalizationReadinessRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<PurgeResourceDeploymentsOutcome>
    PurgeExactDeletedResourceDeployment(
    PurgeExactDeletedResourceDeploymentRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public sealed record GetResourceDeploymentFinalizationReadinessRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId);

public sealed record PurgeDeletedResourceDeploymentsRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId);

public sealed record GetExactResourceDeploymentFinalizationReadinessRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId);

public sealed record PurgeExactDeletedResourceDeploymentRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId);
