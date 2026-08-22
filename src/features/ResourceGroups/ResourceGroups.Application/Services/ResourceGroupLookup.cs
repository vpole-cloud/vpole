using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Services;

public sealed class ResourceGroupLookup(IResourceGroupDbContext dbContext)
    : IResourceGroupLookup
{
    public async Task<Result<ResourceGroupOwnership, ResourceGroupOwnershipError>>
        GetOwnershipOutcomeAsync(
            string tenantId,
            string resourceGroupId,
            CancellationToken cancellationToken = default)
    {
        var resourceGroup = await dbContext.ResourceGroups
            .Find(rg => rg.Id == resourceGroupId && rg.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (resourceGroup is null)
        {
            return new Err<ResourceGroupOwnershipError>(
                new ResourceGroupOwnershipError(
                    new ResourceGroupOwnershipNotFound(resourceGroupId)));
        }

        Result<ResourceGroupOwnership, ResourceGroupOwnershipError> lifecycle =
            resourceGroup.Lifecycle switch
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
                $"Unsupported resource group lifecycle '{resourceGroup.Lifecycle.GetType().Name}'.")
        };

        return lifecycle;
    }

    public async Task<Result<ResourceGroupOwnership>> GetOwnershipAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await dbContext.ResourceGroups
            .Find(rg => rg.Id == resourceGroupId && rg.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        return resourceGroup is null
            ? Failures.NotFound($"Resource group '{resourceGroupId}' was not found.", resourceGroupId)
            : new ResourceGroupOwnership(
                resourceGroup.Id,
                resourceGroup.TenantId,
                resourceGroup.DefaultIsolationScopeId,
                resourceGroup.Lifecycle switch
                {
                    ActiveResourceGroupLifecycleState => ResourceGroupOwnershipLifecycle.Active,
                    DeletingResourceGroupLifecycleState => ResourceGroupOwnershipLifecycle.Deleting,
                    _ => throw new InvalidOperationException(
                        $"Unsupported resource group lifecycle '{resourceGroup.Lifecycle.GetType().Name}'.")
                });
    }

    public async Task<Result<ResourceGroupPlacement>> GetPlacementAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await dbContext.ResourceGroups
            .Find(rg => rg.Id == resourceGroupId && rg.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (resourceGroup is null)
            return Failures.NotFound($"Resource group '{resourceGroupId}' was not found.", resourceGroupId);
        if (resourceGroup.Lifecycle is not ActiveResourceGroupLifecycleState)
            return Failures.Conflict("The resource group is being deleted.");

        return new ResourceGroupPlacement(
            resourceGroup.Id,
            resourceGroup.TenantId,
            resourceGroup.KubernetesNamespace);
    }
}
