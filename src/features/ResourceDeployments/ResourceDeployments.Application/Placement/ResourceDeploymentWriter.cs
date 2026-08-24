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
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    ResolveResourceGroupPlacements resolveResourceGroupPlacements,
    FenceDeploymentTargetWorkloadConfigurations fenceDeploymentTargetWorkloadConfigurations,
    FenceResolvedComputeProfiles fenceResolvedComputeProfiles,
    FenceStorageProfileResolution fenceStorageProfileResolution,
    TimeProvider timeProvider)
{
    public async Task<Result<PlacementSummaryDto, CreateResourceDeploymentError>> InvokeAsync(
        CreateResourceDeploymentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await InvokeSetAsync(
            new CreateResourceDeploymentSetRequest([request]),
            session,
            cancellationToken);
        return result.TryGetValue(out var deployments, out var error)
            ? new Ok<PlacementSummaryDto>(deployments.Single())
            : new Err<CreateResourceDeploymentError>(error);
    }

    public async Task<Result<
        IReadOnlyList<PlacementSummaryDto>,
        CreateResourceDeploymentError>> InvokeSetAsync(
        CreateResourceDeploymentSetRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureTransaction(session);
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
        if (targetFence is Err<DeploymentTargetWorkloadConfigurationFenceRejected>)
        {
            return new Err<CreateResourceDeploymentError>(
                new CreateResourceDeploymentError(
                    new ResourceDeploymentTargetConfigurationChanged(
                        targetFenceRequests
                            .Select(request => request.DeploymentTargetId)
                            .ToArray())));
        }

        var computeFenceRequests = request.Deployments
            .Select(item => new ComputeProfileResolutionFenceRequest(
                item.Compute.ComputeProfileId,
                item.Compute.ComputeProfileRevision,
                item.Compute.DeploymentTargetId,
                item.TenantId,
                item.ResourceType))
            .Distinct()
            .ToArray();
        var computeFence = await fenceResolvedComputeProfiles(
            computeFenceRequests,
            session,
            cancellationToken);
        if (computeFence is Err<ComputeProfileResolutionFenceRejected>)
        {
            return new Err<CreateResourceDeploymentError>(
                new CreateResourceDeploymentError(
                    new ResourceDeploymentComputeConfigurationChanged(
                        computeFenceRequests
                            .Select(request => request.ComputeProfileId)
                            .ToArray())));
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
            if (storageFence is Err<StorageProfileResolutionFenceRejected>)
            {
                return new Err<CreateResourceDeploymentError>(
                    new CreateResourceDeploymentError(
                        new ResourceDeploymentStorageConfigurationChanged(
                            storage.StorageProfileId)));
            }
        }

        var placementResult = await resolveResourceGroupPlacements(
            first.TenantId,
            first.ResourceGroupId,
            first.IsolationScopeId,
            request.Deployments
                .Select(item => item.Target.DeploymentTargetId)
                .ToArray(),
            session,
            cancellationToken);
        return placementResult switch
        {
            Err<ResourceGroupPlacementError> placementFailure =>
                new Err<CreateResourceDeploymentError>(
                    MapPlacementFailure(first.ResourceGroupId, placementFailure.Value)),
            Ok<IReadOnlyList<ResourcePlacement>> success => await PersistResolvedDeploymentsAsync(
                request,
                success.Value,
                session,
                cancellationToken)
        };
    }

    private async Task<Result<
        IReadOnlyList<PlacementSummaryDto>,
        CreateResourceDeploymentError>> PersistResolvedDeploymentsAsync(
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
            return new Err<CreateResourceDeploymentError>(
                new CreateResourceDeploymentError(
                    new ResourcePlacementDeleting(
                        deleting.DeploymentTargetId)));
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
        await resourceDeployments.InsertManyAsync(
            session,
            deployments,
            cancellationToken: cancellationToken);
        await storeOutboxWrites(
            session,
            deployments
                .Where(deployment => deployment.State is PendingResourceDeploymentState)
                .Select(deployment => deployment.ToCurrentRequestWrite())
                .ToArray(),
            cancellationToken);

        return new Ok<IReadOnlyList<PlacementSummaryDto>>(
            deployments
                .Select(deployment => deployment.ToPlacementSummaryDto())
            .ToArray());
    }

    private static CreateResourceDeploymentError MapPlacementFailure(
        string resourceGroupId,
        ResourceGroupPlacementError error) =>
        error switch
        {
            ResourceGroupPlacementResourceGroupNotFound => new(
                new ResourcePlacementResourceGroupNotFound(resourceGroupId)),
            _ => new(new ResourcePlacementUnavailable(resourceGroupId))
        };

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
