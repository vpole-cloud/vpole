using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;

internal sealed class MongoGetResourceGroupOwnership(
    IMongoCollection<ResourceGroup> resourceGroups)
{
    public async Task<Result<ResourceGroupOwnership, ResourceGroupOwnershipError>> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await resourceGroups
            .Find(ResourceGroupFilters.ByTenantAndId(tenantId, resourceGroupId))
            .FirstOrDefaultAsync(cancellationToken);
        if (resourceGroup is null)
        {
            return new Err<ResourceGroupOwnershipError>(
                new ResourceGroupOwnershipError(
                    new ResourceGroupOwnershipNotFound(resourceGroupId)));
        }

        return resourceGroup.Lifecycle switch
        {
            DeletingResourceGroupLifecycleState => new Err<ResourceGroupOwnershipError>(
                new ResourceGroupOwnershipError(
                    new ResourceGroupOwnershipDeleting(resourceGroupId))),
            ActiveResourceGroupLifecycleState => new Ok<ResourceGroupOwnership>(
                new ResourceGroupOwnership(
                    resourceGroup.Id,
                    resourceGroup.TenantId,
                    resourceGroup.DefaultIsolationScopeId,
                    ResourceGroupOwnershipLifecycle.Active)),
            _ => throw new InvalidOperationException(
                $"Resource group '{resourceGroup.Id}' has an unsupported lifecycle state.")
        };
    }
}
