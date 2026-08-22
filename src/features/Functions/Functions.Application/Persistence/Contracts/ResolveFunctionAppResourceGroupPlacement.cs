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

/// <summary>
/// Isolates the transitional ResourceGroups placement contract until that provider publishes
/// a typed placement outcome. Function handlers consume only Function-owned error leaves.
/// </summary>
public sealed class ResourceGroupPlacementResolutionAdapter(
    IResourceGroupLookup resourceGroupLookup)
{
    public async Task<FunctionAppResourceGroupPlacementResolution> InvokeAsync(
        string tenantId,
        string resourceGroupId,
        CancellationToken cancellationToken = default)
    {
        var placement = await resourceGroupLookup.GetPlacementAsync(
            tenantId,
            resourceGroupId,
            cancellationToken);
        if (placement.TryGetValue(out var value, out var failure))
            return value;
        return failure switch
        {
            NotFoundFailure => new FunctionAppResourceGroupNotFound(resourceGroupId),
            ConflictFailure => new FunctionAppResourceGroupDeleting(resourceGroupId),
            _ => throw new InvalidOperationException(
                "Resource group placement returned an unsupported failure.")
        };
    }
}
