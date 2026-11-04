using MongoDB.Driver;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Persistence;

internal sealed class ResourceDeploymentConnectionGuard(
    FenceRegisteredResource fenceRegisteredResource,
    ReconcileConnectionsForResource reconcileConnections)
{
    public async Task<bool> FenceAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken) =>
        await fenceRegisteredResource(
            new FenceRegisteredResourceRequest(
                TenantId: tenantId,
                ResourceTypeId: resourceType,
                ResourceId: resourceId),
            session: session,
            cancellationToken: cancellationToken) is not null;

    public async Task<ResourceDeploymentConnectionConflict?> ReconcileAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var outcome = await reconcileConnections(
            new ReconcileConnectionsForResourceRequest(
                TenantId: tenantId,
                ResourceType: resourceType,
                ResourceId: resourceId),
            session: session,
            cancellationToken: cancellationToken);
        return outcome switch
        {
            ResourceConnectionTopologyValid => null,
            ResourceConnectionSourceHasNoPlacements error =>
                new ResourceDeploymentConnectionConflict(resourceId, error.ConnectionId),
            ResourceConnectionDestinationTargetMissing error =>
                new ResourceDeploymentConnectionConflict(resourceId, error.ConnectionId),
            ResourceConnectionDestinationTargetAmbiguous error =>
                new ResourceDeploymentConnectionConflict(resourceId, error.ConnectionId),
            ResourceConnectionEndpointMissing error =>
                new ResourceDeploymentConnectionConflict(resourceId, error.ConnectionId),
            ResourceConnectionDestinationDeletingConflict error =>
                new ResourceDeploymentConnectionConflict(resourceId, error.ConnectionId),
            ResourceConnectionConcurrentMutation error =>
                new ResourceDeploymentConnectionConflict(resourceId, error.ConnectionId)
        };
    }
}
