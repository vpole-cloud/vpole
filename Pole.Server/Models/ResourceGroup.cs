namespace Pole.Server.Models;

public record ResourceGroup(
    string Name,
    string Version
) : BaseEntity
{
    public string NamespaceName => $"rg-{Id}-{Name}";
}