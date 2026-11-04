using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.StorageProfiles.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Creation;

internal sealed class MongoCreateResourceDeploymentSet(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    ResolveResourceGroupPlacements resolveResourceGroupPlacements,
    FenceDeploymentTargetWorkloadConfigurations fenceDeploymentTargetWorkloadConfigurations,
    FenceResolvedComputeProfiles fenceResolvedComputeProfiles,
    FenceStorageProfileResolution fenceStorageProfileResolution,
    ResourceDeploymentConnectionGuard connectionGuard,
    TimeProvider timeProvider,
    ResourceDeploymentIsolationProjection isolation)
{
    public async Task<CreateResourceDeploymentSetOutcome> InvokeAsync(
        CreateResourceDeploymentSetRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(
            session,
            "Resource deployment creation");
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Deployments);
        if (request.Deployments.Any(static deployment => deployment is null))
        {
            throw new ArgumentException(
                "A deployment set cannot contain null entries.",
                nameof(request));
        }
        if (request.Deployments.Count == 0)
            throw new ArgumentException(
                "At least one resource deployment is required.",
                nameof(request));

        var duplicateDeployment = request.Deployments
            .GroupBy(item => item.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateDeployment is not null)
            throw new ArgumentException(
                "Resource deployment IDs must be unique.",
                nameof(request));
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
            throw new ArgumentException(
                "A deployment set must belong to one tenant, resource, resource revision, resource group, isolation scope, and placement capability.",
                nameof(request));
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
            throw new ArgumentException(
                "Each deployment must have one unique target matching its compute snapshot.",
                nameof(request));
        }

        if (!await connectionGuard.FenceAsync(
                first.TenantId, first.ResourceType, first.ResourceId, session, cancellationToken))
            return new CreateResourceDeploymentError(new ResourcePlacementUnavailable(first.ResourceGroupId));

        var targetFenceRequests = request.Deployments
            .Select(item => new DeploymentTargetWorkloadConfigurationFenceRequest(
                item.Target.DeploymentTargetId,
                item.Target.WorkloadConfigurationRevision))
            .Distinct()
            .ToArray();
        var targetFence = await fenceDeploymentTargetWorkloadConfigurations(
            targetFenceRequests,
            session,
            cancellationToken);
        if (targetFence is DeploymentTargetWorkloadConfigurationFenceRejected)
        {
            return new CreateResourceDeploymentError(
                    new ResourceDeploymentTargetConfigurationChanged(
                        targetFenceRequests
                            .Select(request => request.DeploymentTargetId)
                            .ToArray()));
        }

        var computeFenceRequests = request.Deployments
            .Select(item => new ComputeProfileResolutionFenceRequest(
                item.Compute.ComputeProfileId,
                item.Compute.ComputeProfileRevision,
                item.Compute.DeploymentTargetId,
                item.TenantId,
                item.ResourceType,
                item.PinnedCompute))
            .Distinct()
            .ToArray();
        var computeFence = await fenceResolvedComputeProfiles(
            computeFenceRequests,
            session,
            cancellationToken);
        if (computeFence is ComputeProfileResolutionFenceRejected)
        {
            return new CreateResourceDeploymentError(
                    new ResourceDeploymentComputeConfigurationChanged(
                        computeFenceRequests
                            .Select(request => request.ComputeProfileId)
                            .ToArray()));
        }

        foreach (var storage in request.Deployments
                     .Where(item => item.Storage is not null)
                     .Select(item => new StorageProfileResolutionFenceRequest(
                         item.Storage!.StorageProfileId,
                         item.Storage.StorageProfileRevision,
                         item.Target.DeploymentTargetId,
                         item.Storage.StorageClassName))
                     .Distinct())
        {
            var storageFence = await fenceStorageProfileResolution(
                storage,
                session,
                cancellationToken);
            if (storageFence is StorageProfileResolutionFenceRejected)
            {
                return new CreateResourceDeploymentError(
                        new ResourceDeploymentStorageConfigurationChanged(
                            storage.StorageProfileId));
            }
        }

        var placementResult = await resolveResourceGroupPlacements(
            new ResolveResourceGroupPlacementsRequest(
                TenantId: first.TenantId,
                ResourceGroupId: first.ResourceGroupId,
                IsolationScopeId: first.IsolationScopeId,
                DeploymentTargetIds: request.Deployments
                .Select(item => item.Target.DeploymentTargetId)
                .ToArray()),
            session: session,
            cancellationToken: cancellationToken);
        return placementResult switch
        {
            ResourceGroupPlacementError placementFailure =>
                MapPlacementFailure(first.ResourceGroupId, placementFailure),
            ResourcePlacements success => await PersistResolvedDeploymentsAsync(
                request,
                success.Items,
                session,
                cancellationToken)
        };
    }

    private async Task<CreateResourceDeploymentSetOutcome> PersistResolvedDeploymentsAsync(
        CreateResourceDeploymentSetRequest request,
        IReadOnlyList<ResourcePlacement> placements,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (placements.Count != request.Deployments.Count)
            throw new InvalidOperationException("Resource placement returned an incomplete deployment set.");
        if (placements.Any(placement =>
                placement.State is ResourcePlacementState.Deleting or
                    ResourcePlacementState.Deleted))
        {
            var deleting = placements.First(placement =>
                placement.State is ResourcePlacementState.Deleting or
                    ResourcePlacementState.Deleted);
            return new CreateResourceDeploymentError(
                    new ResourcePlacementDeleting(
                        deleting.DeploymentTargetId));
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
                        item.FixedReplicas,
                        item.DeferredActivation));
            })
            .ToArray();
        await resourceDeployments.InsertManyAsync(
            session,
            deployments,
            cancellationToken: cancellationToken);
        var first = request.Deployments[0];
        var connectionConflict = await connectionGuard.ReconcileAsync(
            first.TenantId, first.ResourceType, first.ResourceId, session, cancellationToken);
        if (connectionConflict is not null)
            return new CreateResourceDeploymentError(connectionConflict);

        await storeOutboxWrites(
            session,
            deployments
                .Where(deployment => deployment.State is PendingResourceDeploymentState)
                .Select(deployment => deployment.ToCurrentRequestWrite())
                .ToArray(),
            cancellationToken);

        return new CreatedResourceDeployments(
            await isolation.ProjectAsync(deployments, cancellationToken));
    }

    private static CreateResourceDeploymentError MapPlacementFailure(
        string resourceGroupId,
        ResourceGroupPlacementError error) =>
        error switch
        {
            ResourceGroupPlacementResourceGroupNotFound => new(
                new ResourcePlacementResourceGroupNotFound(resourceGroupId)),
            ResourceGroupPlacementResourceGroupDeleting
            or ResourceGroupPlacementIsolationScopeNotFound
            or ResourceGroupPlacementIsolationScopeDeleting
            or ResourceGroupPlacementDeploymentTargetRejected
            or ResourceGroupPlacementDeploymentTargetReferenceRejected
            or ResourceGroupPlacementTargetResolutionChanged
            or ResourceGroupPlacementChanged => new(new ResourcePlacementUnavailable(resourceGroupId))
        };

}
