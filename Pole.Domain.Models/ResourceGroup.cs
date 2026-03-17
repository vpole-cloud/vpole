using System.Collections.Immutable;
using Pole.Domain.Models.Abstractions;

namespace Pole.Domain.Models;

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