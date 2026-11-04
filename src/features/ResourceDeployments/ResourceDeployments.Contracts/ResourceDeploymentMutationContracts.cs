using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record ResourceDeploymentMutationRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId,
    ComputeProfileReference ExpectedCompute,
    ResourceDeploymentComputeConfiguration? ReplacementCompute = null);

public sealed record ResourceDeploymentMutationNotFound(
    string ResourceId,
    string? ResourceDeploymentId = null);

public sealed record ResourceDeploymentChanged(
    string ResourceId,
    string? ResourceDeploymentId = null);

public sealed record ResourceDeploymentCardinalityConflict(string ResourceId);

public sealed record ResourceDeploymentPendingDeletion(
    string ResourceId,
    string? ResourceDeploymentId = null);

public sealed record ResourceDeploymentPlacementUnavailable(
    string ResourceId,
    string? ResourceDeploymentId = null);

public sealed record ResourceDeploymentReplacementTargetMismatch(
    string ResourceId,
    string? ResourceDeploymentId = null);

public sealed record ResourceDeploymentIngressConfigurationChanged(
    string ResourceId,
    string ResourceDeploymentId);

public sealed record ResourceDeploymentRetryUnavailable(
    string ResourceId,
    string ResourceDeploymentId);

public sealed record ResourceDeploymentConnectionConflict(
    string ResourceId,
    string ConnectionId);

public union ResourceDeploymentMutationError(
    ResourceDeploymentConnectionConflict,
    ResourceDeploymentMutationNotFound,
    ResourceDeploymentChanged,
    ResourceDeploymentCardinalityConflict,
    ResourceDeploymentPendingDeletion,
    ResourceDeploymentPlacementUnavailable,
    ResourceDeploymentReplacementTargetMismatch,
    ResourceDeploymentIngressConfigurationChanged,
    ResourceDeploymentRetryUnavailable,
    ResourceDeploymentComputeConfigurationChanged);

public union ResourceDeploymentMutationOutcome(
    PlacementSummaryDto,
    ResourceDeploymentMutationError);

public sealed record MutatedResourceDeployments(IReadOnlyList<PlacementSummaryDto> Placements);
public union ResourceDeploymentSetMutationOutcome(
    MutatedResourceDeployments,
    ResourceDeploymentMutationError);

public delegate Task<ResourceDeploymentMutationOutcome>
    RequestResourceDeploymentApply(
    ResourceDeploymentMutationRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<ResourceDeploymentMutationOutcome>
    RequestResourceDeploymentDeletion(
    ResourceDeploymentMutationRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

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
public delegate Task<ResourceDeploymentMutationOutcome>
    RetryExactResourceDeployment(
    ResourceDeploymentExactRetryRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public sealed record ResourceDeploymentSetApplyItem(
    string ResourceDeploymentId,
    long ExpectedDeploymentRevision,
    ComputeProfileReference ExpectedCompute,
    ResourceDeploymentIngressChange IngressChange,
    ResourceDeploymentComputeConfiguration? ReplacementCompute = null,
    int? FixedReplicas = null,
    bool PinnedCompute = false);

public abstract record ResourceDeploymentIngressChange;

public sealed record PreserveIngress : ResourceDeploymentIngressChange;

public sealed record ClearIngress : ResourceDeploymentIngressChange;

public sealed record ReplaceIngress(ResourceDeploymentIngressConfiguration Configuration)
    : ResourceDeploymentIngressChange;

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

public delegate Task<ResourceDeploymentMutationOutcome>
    RequestExactResourceDeploymentApply(
    ResourceDeploymentExactMutationRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<ResourceDeploymentMutationOutcome>
    RequestExactResourceDeploymentDeletion(
    ResourceDeploymentExactMutationRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public delegate Task<ResourceDeploymentSetMutationOutcome>
    RequestResourceDeploymentSetApply(
        ResourceDeploymentSetApplyRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public delegate Task<ResourceDeploymentSetMutationOutcome>
    RequestResourceDeploymentSetDeletion(
        ResourceDeploymentSetDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default);

public union AdvanceResourceDeploymentRevisionOutcome(
    MutatedResourceDeployments,
    AdvanceResourceDeploymentRevisionError);

public delegate Task<AdvanceResourceDeploymentRevisionOutcome> AdvanceResourceDeploymentRevision(
    ResourceDeploymentResourceRevisionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

public sealed record ResourceDeploymentsNotFound(string ResourceId);

public sealed record ResourceDeploymentsHaveNoActivePlacement(string ResourceId);

public sealed record ResourceDeploymentRevisionConflict(string ResourceId);

public union AdvanceResourceDeploymentRevisionError(
    ResourceDeploymentsNotFound,
    ResourceDeploymentsHaveNoActivePlacement,
    ResourceDeploymentRevisionConflict);
