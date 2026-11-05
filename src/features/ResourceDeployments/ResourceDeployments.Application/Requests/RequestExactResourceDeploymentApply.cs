using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Application.Admission;
using Pole.Features.ResourceDeployments.Application;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Requests;

internal sealed class MongoRequestExactResourceDeploymentApply(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    LoadResourcePlacementInTransaction loadResourcePlacement,
    FenceResolvedComputeProfiles fenceResolvedComputeProfiles,
    TimeProvider timeProvider,
    ResourceDeploymentIsolationProjection isolation)
{
    public async Task<ResourceDeploymentMutationOutcome> InvokeAsync(
        ResourceDeploymentExactMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(
            session,
            "Resource deployment mutations");
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await ResourceDeploymentMutationSupport.LoadExactAsync(
            resourceDeployments,
            request,
            session,
            cancellationToken);
        if (loaded is ResourceDeploymentMutationError loadFailure)
            return loadFailure;
        var current = (ResourceDeployment)loaded.Value;
        if (current.Revision != request.ExpectedDeploymentRevision)
        {
            return new ResourceDeploymentMutationError(
                new ResourceDeploymentChanged(request.ResourceId, request.ResourceDeploymentId));
        }

        var computeExpectation = ResourceDeploymentMutationSupport.EnsureExpectedCompute(
            request.ResourceId,
            request.ExpectedCompute,
            current);
        if (computeExpectation is not null)
            return computeExpectation.Value;
        if (current.DesiredOperation == ResourceDeploymentOperation.Delete ||
            current.State is DeletedResourceDeploymentState)
        {
            return new ResourceDeploymentMutationError(
                new ResourceDeploymentPendingDeletion(
                    request.ResourceId,
                    request.ResourceDeploymentId));
        }

        var runtimeCell = await loadResourcePlacement(
            new LoadResourcePlacementInTransactionRequest(
                TenantId: request.TenantId,
                RuntimeCellId: current.RuntimeCellId),
            session: session,
            cancellationToken: cancellationToken);
        if (runtimeCell is null ||
            !string.Equals(
                runtimeCell.DeploymentTargetId,
                current.ResolvedPlacement.DeploymentTargetId,
                StringComparison.Ordinal) ||
            !string.Equals(
                runtimeCell.KubernetesNamespace,
                current.ResolvedPlacement.KubernetesNamespace,
                StringComparison.Ordinal))
        {
            return new ResourceDeploymentMutationError(
                new ResourceDeploymentPlacementUnavailable(
                    request.ResourceId,
                    request.ResourceDeploymentId));
        }

        var placement = current.ResolvedPlacement;
        if (request.ReplacementCompute is not null)
        {
            if (!string.Equals(
                    request.ReplacementCompute.DeploymentTargetId,
                    placement.DeploymentTargetId,
                    StringComparison.Ordinal))
            {
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentReplacementTargetMismatch(
                        request.ResourceId,
                        request.ResourceDeploymentId));
            }

            var computeFence = await fenceResolvedComputeProfiles(
                [new ComputeProfileResolutionFenceRequest(
                    request.ReplacementCompute.ComputeProfileId,
                    request.ReplacementCompute.ComputeProfileRevision,
                    request.ReplacementCompute.DeploymentTargetId,
                    request.TenantId,
                    request.ResourceType)],
                session,
                cancellationToken);
            if (computeFence is ComputeProfileResolutionFenceRejected)
            {
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentComputeConfigurationChanged(
                        [request.ReplacementCompute.ComputeProfileId]));
            }

            placement = placement with { Compute = request.ReplacementCompute.ToDomain() };
        }

        if (request.ReplacementIngress is not null)
        {
            if (!ResourceDeploymentMutationSupport.HasSameIngressIdentity(
                    current.ResolvedPlacement.Ingress,
                    request.ReplacementIngress))
            {
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentIngressConfigurationChanged(
                        request.ResourceId,
                        request.ResourceDeploymentId));
            }

            placement = placement with { Ingress = request.ReplacementIngress.ToDomain() };
        }

        var updated = current.RequestReconciliation(
            placement,
            runtimeCell.State == ResourcePlacementState.Ready,
            timeProvider.GetUtcNow().UtcDateTime,
            request.ResourceRevision,
            request.FixedReplicas);
        return await ResourceDeploymentMutationSupport.ReplaceAndEnqueueAsync(
            resourceDeployments,
            storeOutboxWrites,
            isolation,
            current,
            updated,
            session,
            cancellationToken);
    }
}
