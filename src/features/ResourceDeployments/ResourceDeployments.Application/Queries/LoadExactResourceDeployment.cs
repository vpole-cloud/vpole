using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Queries;

internal sealed class MongoLoadExactResourceDeployment(
    IMongoCollection<ResourceDeployment> resourceDeployments,
    ResourceDeploymentIsolationProjection isolation
)
{
    public async Task<PlacementSummaryDto?> InvokeAsync(
        LoadExactResourceDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var deployment = await resourceDeployments
            .Find(
                ResourceDeploymentFilters.ForExactDeployment(
                    request.TenantId,
                    request.ResourceType,
                    request.ResourceId,
                    request.DeploymentId))
            .FirstOrDefaultAsync(cancellationToken);
        return deployment is null ? null : await isolation.ProjectAsync(deployment, cancellationToken);
    }
}
