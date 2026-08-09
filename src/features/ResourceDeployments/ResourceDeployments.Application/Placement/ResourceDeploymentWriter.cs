using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.StorageProfiles.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Placement;

internal sealed class ResourceDeploymentWriter(
    IResourceDeploymentDbContext dbContext,
    IResourcePlacementService placementService,
    IDeploymentTargetWorkloadConfigurationFence targetConfigurationFence,
    IComputeProfileResolutionFence computeProfileFence,
    IStorageProfileResolutionFence storageConfigurationFence,
    TimeProvider timeProvider)
{
    public async Task<Result<PlacementSummaryDto>> InvokeAsync(
        CreateResourceDeploymentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        if (!string.Equals(
                request.Compute.DeploymentTargetId,
                request.Target.DeploymentTargetId,
                StringComparison.Ordinal))
        {
            return Failures.Conflict(
                "The resolved compute profile belongs to a different deployment target.");
        }

        var targetFence = await targetConfigurationFence
            .FenceAsync(
                request.Target.DeploymentTargetId,
                request.Target.WorkloadConfigurationRevision,
                session,
                cancellationToken);
        if (targetFence is not ISuccess)
            return (Failure)targetFence;
        var computeFence = await computeProfileFence.FenceAsync(
            new ComputeProfileResolutionFenceRequest(
                request.Compute.ComputeProfileId,
                request.Compute.ComputeProfileRevision,
                request.Compute.DeploymentTargetId,
                request.TenantId,
                request.ResourceType),
            session,
            cancellationToken);
        if (computeFence is not ISuccess)
            return (Failure)computeFence;
        if (request.Storage is not null)
        {
            var storageFence = await storageConfigurationFence.FenceAsync(
                new StorageProfileResolutionFenceRequest(
                    request.Storage.StorageProfileId,
                    request.Storage.StorageProfileRevision,
                    request.Target.DeploymentTargetId,
                    request.Storage.StorageClassName),
                session,
                cancellationToken);
            if (storageFence is not ISuccess)
                return (Failure)storageFence;
        }

        var placementResult = await placementService.ResolveOrCreateAsync(
            request.TenantId,
            request.ResourceGroupId,
            request.IsolationScopeId,
            request.Target.DeploymentTargetId,
            session,
            cancellationToken);
        if (!placementResult.TryGetValue(out var placement, out var failure))
            return failure;
        if (!string.Equals(
                placement.DeploymentTargetId,
                request.Target.DeploymentTargetId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Resource placement resolved a different deployment target than the fenced target.");
        }
        if (placement.State is ResourcePlacementState.Deleting or
            ResourcePlacementState.Deleted)
        {
            return Failures.Conflict(
                "The selected runtime cell is being deleted.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var resolvedPlacement = ResolvedResourcePlacement.Create(
            placement.RuntimeCellId,
            placement.DeploymentTargetId,
            placement.KubernetesNamespace,
            placement.RuntimeCellGeneration,
            request.Target.WorkloadConfigurationRevision,
            request.Target.Ingress.ToDomain(),
            request.Storage.ToDomain(),
            request.Compute.ToDomain(),
            now);
        var deployment = ResourceDeploymentFactory.Create(
            request.TenantId,
            request.ResourceType,
            request.ResourceId,
            request.ResourceGroupId,
            placement.IsolationScopeId,
            resolvedPlacement,
            request.PlacementCapability,
            runtimeCellReady: placement.State == ResourcePlacementState.Ready,
            new ResourceDeploymentFactory.CreateOptions(
                request.DeploymentId,
                now));
        await dbContext.ResourceDeployments.InsertOneAsync(
            session,
            deployment,
            cancellationToken: cancellationToken);
        if (deployment.State is PendingResourceDeploymentState)
        {
            await dbContext.StoreEventAsync(
                deployment.ToCurrentRequestWrite(),
                session,
                cancellationToken);
        }

        return deployment.ToPlacementSummaryDto();
    }

    private static void EnsureTransaction(IClientSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsInTransaction)
        {
            throw new InvalidOperationException(
                "Resource deployment creation must run inside the logical resource transaction.");
        }
    }
}
