using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Placement;

/// <summary>
/// Temporary wave 1 adapter for creation consumers that still use shared
/// Failure results.
/// </summary>
internal sealed class ResourceDeploymentLegacyCreationAdapters(
    ResourceDeploymentWriter writer)
{
    public async Task<Result<PlacementSummaryDto>> CreateAsync(
        CreateResourceDeploymentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) =>
        ToLegacy(await writer.InvokeAsync(request, session, cancellationToken));

    private static Result<T> ToLegacy<T>(
        Result<T, CreateResourceDeploymentError> result)
        where T : notnull => result switch
        {
            Ok<T> success => new Success<T>(success.Value),
            Err<CreateResourceDeploymentError> error => ToFailure(error.Value)
        };

    private static Failure ToFailure(
        CreateResourceDeploymentError error) => error switch
        {
            ResourceDeploymentTargetConfigurationChanged => Failures.Conflict(
                "The deployment target workload configuration changed or no longer accepts workloads."),
            ResourceDeploymentComputeConfigurationChanged => Failures.Conflict(
                "The compute profile changed or is no longer available."),
            ResourceDeploymentStorageConfigurationChanged => Failures.Conflict(
                "The storage profile changed or is no longer available."),
            ResourcePlacementResourceGroupNotFound notFound => Failures.NotFound(
                "Resource group not found.",
                notFound.ResourceGroupId),
            ResourcePlacementUnavailable => Failures.Conflict(
                "Resource placement could not be resolved."),
            ResourcePlacementDeleting => Failures.Conflict(
                "A selected runtime cell is being deleted.")
        };
}
