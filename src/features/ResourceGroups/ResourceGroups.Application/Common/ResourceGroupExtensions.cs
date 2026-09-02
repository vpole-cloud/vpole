using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Common;

public static class ResourceGroupExtensions
{
    public static ResourceGroupDto ToDto(this ResourceGroup rg, long resourceCount) =>
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
            ResourceCount: resourceCount,
            Tags: rg.Tags
        );

    public static ResourceRegistrationDto ToDto(this ResourceRegistration registration) =>
        new(
            registration.Id,
            registration.ResourceId,
            registration.ResourceTypeId,
            registration.DisplayName,
            registration.OwningPluginId,
            registration.CreatedAtUtc,
            registration.UpdatedAtUtc);
}
