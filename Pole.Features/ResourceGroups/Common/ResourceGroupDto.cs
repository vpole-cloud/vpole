namespace Pole.Features.ResourceGroups.Common;

public sealed record ResourceGroupDto(
    string Id,
    string Name,
    string Description,
    IEnumerable<ShortResourceInfoDto> Resources,
    IEnumerable<string> Tags
);

public sealed record ShortResourceInfoDto(
    string Id,
    string Name,
    string Type
);