using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Application.Persistence;

public delegate Task<bool> IsPrivateAccessGrantRouteAvailable(
    string tenantId,
    ResourceTypeId resourceType,
    string resourceId,
    string deploymentId,
    string endpointId,
    CancellationToken cancellationToken = default);

internal sealed class MongoIsPrivateAccessGrantRouteAvailable(
    LoadExactResourceDeploymentRuntimePlacement loadPlacement,
    PrivateAccessEndpointCatalog endpoints)
{
    public async Task<bool> InvokeAsync(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string deploymentId,
        string endpointId,
        CancellationToken cancellationToken = default)
    {
        var placement = await loadPlacement(
            tenantId, resourceType, resourceId, deploymentId, cancellationToken);
        if (placement is not { Status: "Applied", DesiredOperation: "Apply" })
            return false;

        var endpoint = await endpoints.ResolveReadOnlyAsync(
            tenantId, resourceType, resourceId, endpointId, cancellationToken);
        return endpoint is not null;
    }
}
