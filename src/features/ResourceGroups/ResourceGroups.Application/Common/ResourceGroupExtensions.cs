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
            Resources: rg.Resources.Select(r => new ShortResourceInfoDto(
                Id: r.Id,
                Name: r.Name,
                Type: r.Type
            )),
            Tags: rg.Tags
        );
}
