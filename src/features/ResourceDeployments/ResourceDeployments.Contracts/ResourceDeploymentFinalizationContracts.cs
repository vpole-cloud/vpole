using MongoDB.Driver;
using Pole.ResultType;
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

public delegate Task<ResourceDeploymentFinalizationReadiness>
    GetResourceDeploymentFinalizationReadiness(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<Result<Unit, ResourceDeploymentFinalizationConflict>>
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

public delegate Task<Result<Unit, ResourceDeploymentFinalizationConflict>>
    PurgeExactDeletedResourceDeployment(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    string resourceDeploymentId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

/// <summary>
/// Temporary shared-result adapters for wave 1 consumers.
/// </summary>
public delegate Task<Result> PurgeDeletedResourceDeploymentsLegacy(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<Result> PurgeExactDeletedResourceDeploymentLegacy(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    string resourceDeploymentId,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
