using Pole.Features.Functions.Application.Common;
using Pole.Features.ResourceGroups.Contracts;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Persistence.Contracts;

public union FunctionAppResourceGroupPlacementResolution(
    ResourceGroupPlacement,
    FunctionAppResourceGroupNotFound,
    FunctionAppResourceGroupDeleting);

public delegate Task<FunctionAppResourceGroupPlacementResolution>
    ResolveFunctionAppResourceGroupPlacement(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default);

public sealed class ResourceGroupPlacementResolutionAdapter(
    GetResourceGroupOwnership getResourceGroupOwnership)
{
    public async Task<FunctionAppResourceGroupPlacementResolution> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default)
    {
        var ownership = await getResourceGroupOwnership(
            tenantId,
            resourceGroupId,
            cancellationToken);
        return ownership switch
        {
            Ok<ResourceGroupOwnership> success => new ResourceGroupPlacement(
                success.Value.ResourceGroupId,
                success.Value.TenantId,
                success.Value.KubernetesNamespace),
            Err<ResourceGroupOwnershipError> { Value: ResourceGroupOwnershipNotFound } =>
                new FunctionAppResourceGroupNotFound(resourceGroupId),
            Err<ResourceGroupOwnershipError> { Value: ResourceGroupOwnershipDeleting } =>
                new FunctionAppResourceGroupDeleting(resourceGroupId)
        };
    }
}
