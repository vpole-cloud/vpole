using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Requests;

internal sealed class MongoRequestResourceDeploymentApply(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    StoreOutboxWrites storeOutboxWrites,
    LoadResourcePlacementInTransaction loadResourcePlacement,
    FenceResolvedComputeProfiles fenceResolvedComputeProfiles,
    TimeProvider timeProvider,
    ResourceDeploymentIsolationProjection isolation
)
{
    public async Task<ResourceDeploymentMutationOutcome> InvokeAsync(
        ResourceDeploymentMutationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(session, "Resource deployment mutations");
        ArgumentNullException.ThrowIfNull(request);

        var loaded = await ResourceDeploymentMutationSupport.LoadSingleAsync(
            resourceDeployments,
            request,
            session,
            cancellationToken);
        if (loaded is ResourceDeploymentMutationError loadFailure)
            return loadFailure;
        var current = (ResourceDeployment)loaded.Value;
        var computeExpectation = ResourceDeploymentMutationSupport.EnsureExpectedCompute(
            request.ResourceId,
            request.ExpectedCompute,
            current);
        if (computeExpectation is not null)
            return computeExpectation.Value;
        if (current.DesiredOperation == ResourceDeploymentOperation.Delete ||
            current.State is DeletedResourceDeploymentState)
            return new ResourceDeploymentMutationError(new ResourceDeploymentPendingDeletion(request.ResourceId));

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
                StringComparison.Ordinal) || !string.Equals(
                runtimeCell.KubernetesNamespace,
                current.ResolvedPlacement.KubernetesNamespace,
                StringComparison.Ordinal))
            return new ResourceDeploymentMutationError(new ResourceDeploymentPlacementUnavailable(request.ResourceId));

        var placement = current.ResolvedPlacement;
        if (request.ReplacementCompute is not null)
        {
            if (!string.Equals(
                    request.ReplacementCompute.DeploymentTargetId,
                    current.ResolvedPlacement.DeploymentTargetId,
                    StringComparison.Ordinal))
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentReplacementTargetMismatch(request.ResourceId));

            var computeFence = await fenceResolvedComputeProfiles(
                [
                    new ComputeProfileResolutionFenceRequest(
                        request.ReplacementCompute.ComputeProfileId,
                        request.ReplacementCompute.ComputeProfileRevision,
                        request.ReplacementCompute.DeploymentTargetId,
                        request.TenantId,
                        request.ResourceType),
                ],
                session,
                cancellationToken);
            if (computeFence is ComputeProfileResolutionFenceRejected)
                return new ResourceDeploymentMutationError(
                    new ResourceDeploymentComputeConfigurationChanged([request.ReplacementCompute.ComputeProfileId]));

            placement = placement with { Compute = request.ReplacementCompute.ToDomain() };
        }

        var updated = current.RequestReconciliation(
            placement,
            runtimeCell.State == ResourcePlacementState.Ready,
            timeProvider.GetUtcNow().UtcDateTime);
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
