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
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<PurgeResourceDeploymentsOutcome>
    PurgeDeletedResourceDeployments(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<ResourceDeploymentFinalizationReadiness>
    GetExactResourceDeploymentFinalizationReadiness(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<PurgeResourceDeploymentsOutcome>
    PurgeExactDeletedResourceDeployment(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    string resourceDeploymentId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
