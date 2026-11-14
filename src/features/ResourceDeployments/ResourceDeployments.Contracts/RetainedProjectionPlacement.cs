using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

/// <summary>A registered credential projection consumer that runs no workload of its own.</summary>
public interface IProjectionOnlyPlacementAdapter : IResourceDeploymentAdapter;

/// <summary>Inherits target, namespace, group, scope and compute identity from an active, tenant-authorized source deployment.</summary>
public sealed record RetainProjectionPlacementRequest(
    string TenantId,
    ResourceTypeId ConsumerType,
    string ConsumerId,
    string DeploymentId,
    ResourceTypeId SourceType,
    string SourceId,
    string SourceDeploymentId
);

public delegate Task<CreateResourceDeploymentOutcome> RetainProjectionPlacement(
    RetainProjectionPlacementRequest request,
    IClientSessionHandle session,
    CancellationToken ct);
