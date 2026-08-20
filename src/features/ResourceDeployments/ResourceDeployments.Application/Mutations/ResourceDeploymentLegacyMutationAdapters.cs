using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Mutations;

/// <summary>
/// Compatibility boundary for wave 1 consumers that still use the shared
/// Failure model. Remove this type after those consumers include the
/// provider-owned mutation error union in their own outcomes.
/// </summary>
internal sealed class ResourceDeploymentLegacyMutationAdapters(
    ResourceDeploymentMutationWriter writer)
{
    public async Task<Result<PlacementSummaryDto>> RequestApplyAsync(
        ResourceDeploymentMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        ToLegacy(await writer.RequestApplyAsync(request, session, cancellationToken));

    public async Task<Result<PlacementSummaryDto>> RequestDeletionAsync(
        ResourceDeploymentMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        ToLegacy(await writer.RequestDeletionAsync(request, session, cancellationToken));

    public async Task<Result<PlacementSummaryDto>> RequestExactApplyAsync(
        ResourceDeploymentExactMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        ToLegacy(await writer.RequestExactApplyAsync(request, session, cancellationToken));

    public async Task<Result<PlacementSummaryDto>> RequestExactDeletionAsync(
        ResourceDeploymentExactMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        ToLegacy(await writer.RequestExactDeletionAsync(request, session, cancellationToken));

    public async Task<Result<PlacementSummaryDto>> RequestRetryAsync(
        ResourceDeploymentExactRetryRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        ToLegacy(await writer.RequestRetryAsync(request, session, cancellationToken));

    public async Task<Result<IReadOnlyList<PlacementSummaryDto>>> RequestSetApplyAsync(
        ResourceDeploymentSetApplyRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        ToLegacy(await writer.RequestSetApplyAsync(request, session, cancellationToken));

    public async Task<Result<IReadOnlyList<PlacementSummaryDto>>> RequestSetDeletionAsync(
        ResourceDeploymentSetDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        ToLegacy(await writer.RequestSetDeletionAsync(request, session, cancellationToken));

    public async Task<Result<IReadOnlyList<PlacementSummaryDto>>>
        AdvanceResourceRevisionAsync(
            ResourceDeploymentResourceRevisionRequest request,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default)
    {
        var result = await writer.AdvanceResourceRevisionOutcomeAsync(
            request,
            session,
            cancellationToken);
        return result switch
        {
            Ok<IReadOnlyList<PlacementSummaryDto>> success =>
                new Success<IReadOnlyList<PlacementSummaryDto>>(success.Value),
            Err<AdvanceResourceDeploymentRevisionError> error => error.Value switch
            {
                ResourceDeploymentsNotFound notFound => Failures.NotFound(
                    "Resource deployments were not found.",
                    notFound.ResourceId),
                ResourceDeploymentsHaveNoActivePlacement => Failures.Conflict(
                    "The logical resource has no active resource deployments to advance."),
                ResourceDeploymentRevisionConflict => Failures.Conflict(
                    "A resource deployment changed while the logical resource revision was being advanced.")
            }
        };
    }

    private static Result<T> ToLegacy<T>(
        Result<T, ResourceDeploymentMutationError> result)
        where T : notnull => result switch
        {
            Ok<T> success => new Success<T>(success.Value),
            Err<ResourceDeploymentMutationError> error => ToFailure(error.Value)
        };

    private static Failure ToFailure(
        ResourceDeploymentMutationError error) => error switch
        {
            ResourceDeploymentMutationNotFound notFound => Failures.NotFound(
                "Resource deployment was not found.",
                notFound.ResourceDeploymentId ?? notFound.ResourceId),
            ResourceDeploymentChanged => Failures.Conflict(
                "The resource deployment changed concurrently."),
            ResourceDeploymentCardinalityConflict => Failures.Conflict(
                "The logical resource does not have exactly one deployment."),
            ResourceDeploymentPendingDeletion => Failures.Conflict(
                "A resource deployment pending deletion cannot be reconciled."),
            ResourceDeploymentPlacementUnavailable => Failures.Conflict(
                "The resource deployment runtime-cell placement is unavailable."),
            ResourceDeploymentReplacementTargetMismatch => Failures.Conflict(
                "The replacement compute profile belongs to a different deployment target."),
            ResourceDeploymentIngressConfigurationChanged => Failures.Conflict(
                "The resource deployment ingress configuration changed."),
            ResourceDeploymentRetryUnavailable => Failures.Conflict(
                "The resource deployment operation is not retryable."),
            ResourceDeploymentComputeConfigurationChanged => Failures.Conflict(
                "The resource deployment compute configuration changed.")
        };
}
