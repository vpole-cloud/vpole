using Pole.Features.Shared.Models;

namespace Pole.Features.ResourceGroups.Application.Common;

public sealed record ResourceGroupDto(
    string Id,
    string Name,
    string Description,
    string DefaultIsolationScopeId,
    string Lifecycle,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IEnumerable<ShortResourceInfoDto> Resources,
    IEnumerable<string> Tags
);

public sealed record ShortResourceInfoDto(
    string Id,
    string Name,
    ResourceType Type
);
