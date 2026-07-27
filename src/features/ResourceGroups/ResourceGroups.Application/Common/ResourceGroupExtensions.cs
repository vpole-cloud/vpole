using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Common;

public static class ResourceGroupExtensions
{
    public static ResourceGroupDto ToDto(this ResourceGroup rg) =>
        new ResourceGroupDto(
            Id: rg.Id,
            Name: rg.Name,
            Description: rg.Description,
            DefaultIsolationScopeId: rg.DefaultIsolationScopeId,
            Lifecycle: rg.Lifecycle switch
            {
                ActiveResourceGroupLifecycleState => "active",
                DeletingResourceGroupLifecycleState => "deleting",
                _ => throw new InvalidOperationException(
                    $"Unsupported resource group lifecycle '{rg.Lifecycle.GetType().Name}'.")
            },
            Revision: rg.Revision,
            CreatedAtUtc: rg.CreatedAtUtc,
            UpdatedAtUtc: rg.UpdatedAtUtc,
            Resources: rg.Resources.Select(r => new ShortResourceInfoDto(
                Id: r.Id,
                Name: r.Name,
                Type: r.Type
            )),
            Tags: rg.Tags
        );
}
