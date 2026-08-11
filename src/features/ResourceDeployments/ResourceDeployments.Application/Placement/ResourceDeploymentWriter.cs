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
                now,
                request.ResourceRevision,
                request.FixedReplicas));
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

    public async Task<Result<IReadOnlyList<PlacementSummaryDto>>> InvokeSetAsync(
        CreateResourceDeploymentSetRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Deployments.Count == 0)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Deployments"] = ["At least one resource deployment is required."]
            });
        }

        var duplicateDeployment = request.Deployments
            .GroupBy(item => item.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDeployment is not null)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Deployments"] = ["Resource deployment IDs must be unique."]
            });
        }
        var first = request.Deployments[0];
        if (request.Deployments.Any(item =>
                !string.Equals(item.TenantId, first.TenantId, StringComparison.Ordinal) ||
                !string.Equals(item.ResourceType.Value, first.ResourceType.Value, StringComparison.Ordinal) ||
                !string.Equals(item.ResourceId, first.ResourceId, StringComparison.Ordinal) ||
                !string.Equals(item.ResourceGroupId, first.ResourceGroupId, StringComparison.Ordinal) ||
                !string.Equals(item.IsolationScopeId, first.IsolationScopeId, StringComparison.Ordinal) ||
                item.ResourceRevision != first.ResourceRevision ||
                item.PlacementCapability != first.PlacementCapability))
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Deployments"] =
                    ["A deployment set must belong to one tenant, resource, resource revision, resource group, isolation scope, and placement capability."]
            });
        }
        if (request.Deployments.Any(item => !string.Equals(
                item.Compute.DeploymentTargetId,
                item.Target.DeploymentTargetId,
                StringComparison.Ordinal)) ||
            request.Deployments
                .Select(item => item.Target.DeploymentTargetId)
                .Distinct(StringComparer.Ordinal)
                .Count() != request.Deployments.Count)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Deployments"] =
                    ["Each deployment must have one unique target matching its compute snapshot."]
            });
        }

        var targetFenceRequests = request.Deployments
            .Select(item => new DeploymentTargetWorkloadConfigurationFenceRequest(
                item.Target.DeploymentTargetId,
                item.Target.WorkloadConfigurationRevision))
            .Distinct()
            .ToArray();
        var targetFence = await targetConfigurationFence.FenceManyAsync(
            targetFenceRequests,
            session,
            cancellationToken);
        if (targetFence is not ISuccess)
            return (Failure)targetFence;

        var computeFenceRequests = request.Deployments
            .Select(item => new ComputeProfileResolutionFenceRequest(
                item.Compute.ComputeProfileId,
                item.Compute.ComputeProfileRevision,
                item.Compute.DeploymentTargetId,
                item.TenantId,
                item.ResourceType))
            .Distinct()
            .ToArray();
        var computeFence = await computeProfileFence.FenceManyAsync(
            computeFenceRequests,
            session,
            cancellationToken);
        if (computeFence is not ISuccess)
            return (Failure)computeFence;

        foreach (var storage in request.Deployments
                     .Where(item => item.Storage is not null)
                     .Select(item => new StorageProfileResolutionFenceRequest(
                         item.Storage!.StorageProfileId,
                         item.Storage.StorageProfileRevision,
                         item.Target.DeploymentTargetId,
                         item.Storage.StorageClassName))
                     .Distinct())
        {
            var storageFence = await storageConfigurationFence.FenceAsync(
                storage,
                session,
                cancellationToken);
            if (storageFence is not ISuccess)
                return (Failure)storageFence;
        }

        var placementResult = await placementService.ResolveOrCreateManyAsync(
            first.TenantId,
            first.ResourceGroupId,
            first.IsolationScopeId,
            request.Deployments
                .Select(item => item.Target.DeploymentTargetId)
                .ToArray(),
            session,
            cancellationToken);
        if (!placementResult.TryGetValue(out var placements, out var placementFailure))
            return placementFailure;
        if (placements.Count != request.Deployments.Count)
            throw new InvalidOperationException("Resource placement returned an incomplete deployment set.");
        if (placements.Any(placement =>
                placement.State is ResourcePlacementState.Deleting or
                    ResourcePlacementState.Deleted))
        {
            return Failures.Conflict("A selected runtime cell is being deleted.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var deployments = request.Deployments
            .Select((item, index) =>
            {
                var placement = placements[index];
                if (!string.Equals(
                        placement.DeploymentTargetId,
                        item.Target.DeploymentTargetId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Resource placement returned deployment targets in an unexpected order.");
                }
                return ResourceDeploymentFactory.Create(
                    item.TenantId,
                    item.ResourceType,
                    item.ResourceId,
                    item.ResourceGroupId,
                    placement.IsolationScopeId,
                    ResolvedResourcePlacement.Create(
                        placement.RuntimeCellId,
                        placement.DeploymentTargetId,
                        placement.KubernetesNamespace,
                        placement.RuntimeCellGeneration,
                        item.Target.WorkloadConfigurationRevision,
                        item.Target.Ingress.ToDomain(),
                        item.Storage.ToDomain(),
                        item.Compute.ToDomain(),
                        now),
                    item.PlacementCapability,
                    runtimeCellReady: placement.State == ResourcePlacementState.Ready,
                    new ResourceDeploymentFactory.CreateOptions(
                        item.DeploymentId,
                        now,
                        item.ResourceRevision,
                        item.FixedReplicas));
            })
            .ToArray();
        await dbContext.ResourceDeployments.InsertManyAsync(
            session,
            deployments,
            cancellationToken: cancellationToken);
        await dbContext.StoreEventAsync(
            deployments
                .Where(deployment => deployment.State is PendingResourceDeploymentState)
                .Select(deployment => deployment.ToCurrentRequestWrite()),
            session,
            cancellationToken);

        return deployments
            .Select(deployment => deployment.ToPlacementSummaryDto())
            .ToArray();
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
