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

public sealed record ResourceDeploymentExactMutationRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    long ExpectedDeploymentRevision,
    ComputeProfileReference ExpectedCompute,
    ResourceDeploymentComputeConfiguration? ReplacementCompute = null,
    ResourceDeploymentIngressConfiguration? ReplacementIngress = null,
    int? FixedReplicas = null,
    long? ResourceRevision = null);

public sealed record ResourceDeploymentResourceRevisionRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    long ResourceRevision);

public enum ResourceDeploymentRequestedOperation
{
    Apply,
    Delete
}

public sealed record ResourceDeploymentExactRetryRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    long ExpectedDeploymentRevision,
    ResourceDeploymentRequestedOperation ExpectedOperation);

/// <summary>
/// Retries one exact failed physical deployment inside the caller-owned
/// transaction so the logical resource mutation and retry outbox commit together.
/// </summary>
public interface IResourceDeploymentExactRetryWriter
{
    Task<Result<PlacementSummaryDto>> RequestRetryAsync(
        ResourceDeploymentExactRetryRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public sealed record ResourceDeploymentSetApplyItem(
    string ResourceDeploymentId,
    long ExpectedDeploymentRevision,
    ComputeProfileReference ExpectedCompute,
    ResourceDeploymentComputeConfiguration? ReplacementCompute = null,
    ResourceDeploymentIngressConfiguration? ReplacementIngress = null,
    int? FixedReplicas = null);

public sealed record ResourceDeploymentSetApplyRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    long ResourceRevision,
    IReadOnlyList<ResourceDeploymentSetApplyItem> Deployments);

public sealed record ResourceDeploymentSetDeletionItem(
    string ResourceDeploymentId,
    long ExpectedDeploymentRevision,
    ComputeProfileReference ExpectedCompute);

public sealed record ResourceDeploymentSetDeletionRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    long ResourceRevision,
    IReadOnlyList<ResourceDeploymentSetDeletionItem> Deployments);

/// <summary>
/// Set-aware mutation surface for resources that own more than one physical
/// deployment. Each mutation remains caller-transactional and tenant scoped.
/// </summary>
public interface IResourceDeploymentSetMutationWriter
{
    Task<Result<PlacementSummaryDto>> RequestExactApplyAsync(
        ResourceDeploymentExactMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result<PlacementSummaryDto>> RequestExactDeletionAsync(
        ResourceDeploymentExactMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<PlacementSummaryDto>>> RequestSetApplyAsync(
        ResourceDeploymentSetApplyRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<PlacementSummaryDto>>> RequestSetDeletionAsync(
        ResourceDeploymentSetDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<PlacementSummaryDto>>> AdvanceResourceRevisionAsync(
        ResourceDeploymentResourceRevisionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

}

public interface IResourceDeploymentSetOutcomeMutationWriter
{
    Task<Result<
        IReadOnlyList<PlacementSummaryDto>,
        AdvanceResourceDeploymentRevisionError>> AdvanceResourceRevisionOutcomeAsync(
        ResourceDeploymentResourceRevisionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);
}

public sealed record ResourceDeploymentsNotFound(string ResourceId);

public sealed record ResourceDeploymentsHaveNoActivePlacement(string ResourceId);

public sealed record ResourceDeploymentRevisionConflict(string ResourceId);

public union AdvanceResourceDeploymentRevisionError(
    ResourceDeploymentsNotFound,
    ResourceDeploymentsHaveNoActivePlacement,
    ResourceDeploymentRevisionConflict);
