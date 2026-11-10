using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Requests;

internal sealed class MongoRequestResourceDeploymentSetApply(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    LoadResourcePlacementsInTransaction loadResourcePlacements,
    FenceResolvedComputeProfiles fenceResolvedComputeProfiles,
    TimeProvider timeProvider,
    ResourceDeploymentIsolationProjection isolation
)
{
    public async Task<ResourceDeploymentSetMutationOutcome> InvokeAsync(
        ResourceDeploymentSetApplyRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(session, "Resource deployment mutations");
        ArgumentNullException.ThrowIfNull(request);
        ResourceDeploymentMutationSupport.EnsureSetIsNonEmptyAndUnique(
            request.Deployments,
            deployment => deployment.ResourceDeploymentId,
            nameof(request));
        if (request.Deployments.Any(item => item.IngressChange is null or ReplaceIngress { Configuration: null }))
            throw new ArgumentException("Every deployment must specify a valid ingress change.", nameof(request));

        var deploymentIds = request.Deployments.Select(item => item.ResourceDeploymentId).ToArray();
        var current = await resourceDeployments.Find(
                session,
                ResourceDeploymentFilters.ForResourceDeploymentIds(
                    request.TenantId,
                    request.ResourceType,
                    request.ResourceId,
                    deploymentIds))
            .ToListAsync(cancellationToken);
        if (current.Count != request.Deployments.Count)
            return new ResourceDeploymentMutationError(new ResourceDeploymentMutationNotFound(request.ResourceId));

        var byId = current.ToDictionary(deployment => deployment.Id, StringComparer.Ordinal);
        foreach (var item in request.Deployments)
        {
            var deployment = byId[item.ResourceDeploymentId];
            if (deployment.Revision != item.ExpectedDeploymentRevision ||
                (request.Activation == ResourceDeploymentActivation.KeepPrepared &&
                    deployment.State is not PreparedResourceDeploymentState))
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentChanged(request.ResourceId, item.ResourceDeploymentId));

            var computeExpectation = ResourceDeploymentMutationSupport.EnsureExpectedCompute(
                request.ResourceId,
                item.ExpectedCompute,
                deployment);
            if (computeExpectation is not null)
                return computeExpectation.Value;
            if (deployment.DesiredOperation == ResourceDeploymentOperation.Delete ||
                deployment.State is DeletedResourceDeploymentState)
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentPendingDeletion(request.ResourceId, item.ResourceDeploymentId));
            if (item.ReplacementCompute is not null && !string.Equals(
                    item.ReplacementCompute.DeploymentTargetId,
                    deployment.ResolvedPlacement.DeploymentTargetId,
                    StringComparison.Ordinal))
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentReplacementTargetMismatch(request.ResourceId, item.ResourceDeploymentId));
            if (item.IngressChange is ReplaceIngress replaceIngress &&
                deployment.ResolvedPlacement.Ingress is not null &&
                !ResourceDeploymentMutationSupport.HasSameIngressIdentity(
                    deployment.ResolvedPlacement.Ingress,
                    replaceIngress.Configuration))
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentIngressConfigurationChanged(request.ResourceId, item.ResourceDeploymentId));
        }

        var runtimeCells = await loadResourcePlacements(
            new LoadResourcePlacementsInTransactionRequest(
                TenantId: request.TenantId,
                RuntimeCellIds: current.Select(deployment => deployment.RuntimeCellId).ToArray()),
            session: session,
            cancellationToken: cancellationToken);
        var runtimeCellsById = runtimeCells.ToDictionary(placement => placement.RuntimeCellId, StringComparer.Ordinal);
        if (runtimeCellsById.Count != current.Select(deployment => deployment.RuntimeCellId)
                .Distinct(StringComparer.Ordinal)
                .Count())
            return new ResourceDeploymentMutationError(new ResourceDeploymentPlacementUnavailable(request.ResourceId));

        foreach (var deployment in current)
        {
            var runtimeCell = runtimeCellsById[deployment.RuntimeCellId];
            if (!string.Equals(
                    runtimeCell.DeploymentTargetId,
                    deployment.ResolvedPlacement.DeploymentTargetId,
                    StringComparison.Ordinal) || !string.Equals(
                    runtimeCell.KubernetesNamespace,
                    deployment.ResolvedPlacement.KubernetesNamespace,
                    StringComparison.Ordinal))
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentPlacementUnavailable(request.ResourceId, deployment.Id));
        }

        var computeFenceRequests = request.Deployments
            .Where(item => item.ReplacementCompute is not null)
            .Select(item => new ComputeProfileResolutionFenceRequest(
                item.ReplacementCompute!.ComputeProfileId,
                item.ReplacementCompute.ComputeProfileRevision,
                item.ReplacementCompute.DeploymentTargetId,
                request.TenantId,
                request.ResourceType,
                item.PinnedCompute))
            .Distinct()
            .ToArray();
        if (computeFenceRequests.Length > 0)
        {
            var computeFence = await fenceResolvedComputeProfiles(computeFenceRequests, session, cancellationToken);
            if (computeFence is ComputeProfileResolutionFenceRejected)
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentComputeConfigurationChanged(
                        computeFenceRequests.Select(fence => fence.ComputeProfileId).ToArray()));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var updated = new List<ResourceDeployment>(request.Deployments.Count);
        foreach (var item in request.Deployments)
        {
            var deployment = byId[item.ResourceDeploymentId];
            var placement = deployment.ResolvedPlacement;
            if (item.ReplacementCompute is not null)
                placement = placement with { Compute = item.ReplacementCompute.ToDomain() };
            placement = item.IngressChange switch
            {
                PreserveIngress => placement,
                ClearIngress => placement with { Ingress = null },
                ReplaceIngress replaceIngress => placement with
                {
                    Ingress = replaceIngress.Configuration.ToDomain(),
                },
                _ => throw new ArgumentException("An ingress change is required.", nameof(request)),
            };

            updated.Add(
                request.Activation switch
                {
                    ResourceDeploymentActivation.KeepPrepared => deployment.ConfigurePrepared(
                        placement,
                        now,
                        request.ResourceRevision,
                        item.FixedReplicas ?? deployment.FixedReplicas),
                    ResourceDeploymentActivation.Reconcile => deployment.RequestReconciliation(
                        placement,
                        runtimeCellsById[deployment.RuntimeCellId].State == ResourcePlacementState.Ready,
                        now,
                        request.ResourceRevision,
                        item.FixedReplicas),
                    _ => throw new ArgumentOutOfRangeException(nameof(request)),
                });
        }

        foreach (var deployment in updated)
        {
            var original = byId[deployment.Id];
            var replacement = await resourceDeployments.ReplaceOneAsync(
                session,
                ResourceDeploymentFilters.AtRevision(original),
                deployment,
                cancellationToken: cancellationToken);
            if (replacement.ModifiedCount != 1)
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentChanged(request.ResourceId, original.Id));
        }

        await storeOutboxWrites(
            session,
            updated.Where(deployment => deployment.State is PendingResourceDeploymentState)
                .Select(deployment => deployment.ToCurrentRequestWrite())
                .ToArray(),
            cancellationToken);
        return new MutatedResourceDeployments(await isolation.ProjectAsync(updated, cancellationToken));
    }
}
