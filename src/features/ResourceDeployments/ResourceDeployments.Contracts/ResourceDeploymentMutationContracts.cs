using MongoDB.Driver;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record ResourceDeploymentMutationRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    ComputeProfileReference ExpectedCompute,
    ResourceDeploymentComputeConfiguration? ReplacementCompute = null);

public interface IResourceDeploymentMutationWriter
{
    Task<Result<PlacementSummaryDto>> RequestApplyAsync(
        ResourceDeploymentMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result<PlacementSummaryDto>> RequestDeletionAsync(
        ResourceDeploymentMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}
