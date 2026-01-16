using System.Collections.Immutable;
using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.ResourceGroups.Common;

public record ResourceGroup(
    string Name,
    string Description,
    ImmutableList<ShortResourceInfo> Resources,
    ImmutableList<string> Tags
    ) : BaseEntity
{
    public string NamespaceName => $"rg-{Id}-{Name}";
    
}

public sealed record ShortResourceInfo(
    string Id,
    string Name,
    string Type
);