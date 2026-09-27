using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Reads;

internal delegate Task<ResourceDeployment?> LoadIngressPolicyDeployment(
    ResourceIngressPolicyRequest request,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadIngressPolicyDeployment(
    IMongoCollection<ResourceDeployment> resourceDeployments)
{
    public async Task<ResourceDeployment?> InvokeAsync(
        ResourceIngressPolicyRequest request,
        CancellationToken cancellationToken = default) =>
        await resourceDeployments
            .Find(ResourceDeploymentFilters.ForExactDeployment(
                request.TenantId,
                request.ResourceType,
                request.ResourceId,
                request.ResourceDeploymentId))
            .FirstOrDefaultAsync(cancellationToken);
}
