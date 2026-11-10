namespace Pole.Features.ContainerRegistries.Application.Common;

public sealed class HarborOptions
{
    public const string SectionName = "Harbor";

    public bool Enabled { get; init; }
    public string BaseUrl { get; init; } = "";
    public string RegistryHost { get; init; } = "";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public int RobotDurationDays { get; init; } = -1;

    public bool IsConfigured =>
        Enabled && Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri) &&
        (string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) || string.Equals(
            baseUri.Scheme,
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(RegistryHost) &&
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password) && RobotDurationDays is -1 or > 0;
}
