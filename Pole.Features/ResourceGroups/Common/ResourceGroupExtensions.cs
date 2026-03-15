using Pole.Domain.Models;

namespace Pole.Features.ResourceGroups.Common;

public static class ResourceGroupExtensions
{
    public static ResourceGroupDto ToDto(this ResourceGroup rg) =>
        new ResourceGroupDto(
            Id: rg.Id,
            Name: rg.Name,
            Description: rg.Description,
            Resources: rg.Resources.Select(r => new ShortResourceInfoDto(
                Id: r.Id,
                Name: r.Name,
                Type: r.Type
            )),
            Tags: rg.Tags
        );
}