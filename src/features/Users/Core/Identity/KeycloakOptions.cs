namespace Pole.Features.Users.Identity;

public sealed class KeycloakOptions
{
    public const string SectionName = "KeycloakAdmin";

    public string BaseUrl { get; init; } = "";
    public string Realm { get; init; } = "";
    public string AdminClientId { get; init; } = "";
    public string AdminClientSecret { get; init; } = "";
}
