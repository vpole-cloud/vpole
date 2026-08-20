using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.ResultType;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Common;

/// <summary>
/// Temporary wave 1 boundary from typed ResourceDeployments capabilities to the
/// shared Failure model used by consumers that have not yet migrated.
/// </summary>
internal sealed class ResourceDeploymentLegacyAdapters(
    ResourceDeploymentReferenceLookup references,
    ResourceDeploymentComputeProfileReferenceGuard computeReferences,
    ResourceDeploymentComputeLookup computeLookup,
    ResourceDeploymentFinalizationService finalization)
{
    public async Task<Result> EnsureNoUnfinishedForResourceGroupAsync(
        string tenantId,
        string resourceGroupId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var result = await references.EnsureNoUnfinishedForResourceGroupAsync(
            tenantId,
            resourceGroupId,
            session,
            cancellationToken);
        return result.TryGetValue(out _, out _)
            ? Result.Success()
            : Failures.Conflict(
                "The resource group contains unfinished resource deployments.");
    }

    public async Task<Result> EnsureNoUnfinishedForIsolationScopeAsync(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var result = await references.EnsureNoUnfinishedForIsolationScopeAsync(
            tenantId,
            resourceGroupId,
            isolationScopeId,
            session,
            cancellationToken);
        return result.TryGetValue(out _, out _)
            ? Result.Success()
            : Failures.Conflict(
                "The isolation scope contains unfinished resource deployments.");
    }

    public async Task<Result> EnsureComputeProfileUnreferencedAsync(
        string computeProfileId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var result = await computeReferences.EnsureUnreferencedAsync(
            computeProfileId,
            session,
            cancellationToken);
        return result.TryGetValue(out _, out _)
            ? Result.Success()
            : Failures.Conflict(
                "The compute profile is still referenced by a resource deployment.");
    }

    public async Task<Result<CurrentResourceDeploymentComputeDto>>
        LoadCurrentComputeAsync(
            string tenantId,
            ResourceTypeId resourceType,
            string resourceId,
            CancellationToken cancellationToken = default)
    {
        var result = await computeLookup.GetCurrentAsync(
            tenantId,
            resourceType,
            resourceId,
            cancellationToken);
        return result switch
        {
            Ok<CurrentResourceDeploymentComputeDto> success =>
                new Success<CurrentResourceDeploymentComputeDto>(success.Value),
            Err<LoadCurrentResourceDeploymentComputeError> error => error.Value switch
            {
                CurrentResourceDeploymentComputeNotFound notFound => Failures.NotFound(
                    "Resource deployment not found.",
                    notFound.ResourceId),
                CurrentResourceDeploymentComputeCardinalityConflict => Failures.Conflict(
                    "The logical resource does not have exactly one current compute configuration.")
            }
        };
    }

    public async Task<Result> PurgeDeletedAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var result = await finalization.PurgeDeletedAsync(
            tenantId,
            resourceType,
            resourceId,
            session,
            cancellationToken);
        return result.TryGetValue(out _, out _)
            ? Result.Success()
            : Failures.Conflict(
                "The resource deployment is not ready for finalization.");
    }

    public async Task<Result> PurgeExactDeletedAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceDeploymentId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var result = await finalization.PurgeExactDeletedAsync(
            tenantId,
            resourceType,
            resourceId,
            resourceDeploymentId,
            session,
            cancellationToken);
        return result.TryGetValue(out _, out _)
            ? Result.Success()
            : Failures.Conflict(
                "The resource deployment is not ready for exact finalization.");
    }
}
