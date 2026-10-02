using Pole.Database;
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
    PrivateAccessEndpointCatalog endpoints,
    MongoRunTransaction transactions)
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

        return await transactions.InvokeOutcomeAsync<bool>(
            async (transaction, ct) =>
            {
                var endpoint = await endpoints.ResolveAsync(
                    tenantId, resourceType, resourceId, endpointId,
                    transaction.Session, ct);
                return transaction.Abort(endpoint is not null);
            }, cancellationToken);
    }
}
