using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Services;

public sealed class ResourceGroupLookup(IResourceGroupDbContext dbContext) : IResourceGroupLookup
{
    public async Task<Result<ResourceGroupPlacement>> GetPlacementAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await dbContext.ResourceGroups
            .Find(rg => rg.Id == resourceGroupId && rg.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        return resourceGroup is null
            ? Failures.NotFound($"Resource group '{resourceGroupId}' was not found.", resourceGroupId)
            : new ResourceGroupPlacement(
                resourceGroup.Id,
                resourceGroup.TenantId,
                resourceGroup.KubernetesNamespace);
    }
}
