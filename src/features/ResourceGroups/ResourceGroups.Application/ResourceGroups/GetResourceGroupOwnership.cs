using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Application.Mongo;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.ResourceGroups;

internal sealed class MongoGetResourceGroupOwnership(
    IMongoCollection<ResourceGroup> resourceGroups)
{
    public async Task<GetResourceGroupOwnershipOutcome> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await resourceGroups
            .Find(ResourceGroupFilters.ByTenantAndId(tenantId, resourceGroupId))
            .FirstOrDefaultAsync(cancellationToken);
        if (resourceGroup is null)
        {
            return new ResourceGroupOwnershipError(
                new ResourceGroupOwnershipNotFound(resourceGroupId));
        }

        return resourceGroup.Lifecycle switch
        {
            DeletingResourceGroupLifecycleState => new ResourceGroupOwnershipError(
                new ResourceGroupOwnershipDeleting(resourceGroupId)),
            ActiveResourceGroupLifecycleState =>
                new ResourceGroupOwnership(
                    resourceGroup.Id,
                    resourceGroup.TenantId,
                    resourceGroup.DefaultIsolationScopeId,
                    ResourceGroupOwnershipLifecycle.Active),
            _ => throw new InvalidOperationException(
                $"Resource group '{resourceGroup.Id}' has an unsupported lifecycle state.")
        };
    }
}
