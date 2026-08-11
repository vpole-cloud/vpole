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

public interface IResourceDeploymentFinalizationService
{
    Task<ResourceDeploymentFinalizationReadiness> GetReadinessAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result> PurgeDeletedAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public interface IResourceDeploymentExactFinalizationService
{
    Task<ResourceDeploymentFinalizationReadiness> GetExactReadinessAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result> PurgeExactDeletedAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}
