namespace Pole.Features.GitProviders.Application.Common;

public sealed class GitHubAppOptions
{
    public const string SectionName = "GitHubApp";

    public bool Enabled { get; init; }
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string AppSlug { get; init; } = string.Empty;
    public string PrivateKeyPath { get; init; } = string.Empty;
    public string PublicBaseUrl { get; init; } = string.Empty;
    public string FrontendBaseUrl { get; init; } = "http://localhost:5173";
    public string ApiBaseUrl { get; init; } = "https://api.github.com";
    public string WebBaseUrl { get; init; } = "https://github.com";
    public string ApiVersion { get; init; } = "2026-03-10";

    public bool IsConfigured =>
        Enabled &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret) &&
        !string.IsNullOrWhiteSpace(AppSlug) &&
        !string.IsNullOrWhiteSpace(PrivateKeyPath) &&
        !string.IsNullOrWhiteSpace(PublicBaseUrl);

    public string SetupCallbackUrl => BuildPublicUrl("/api/v1/integrations/github/setup-complete");
    public string OAuthCallbackUrl => BuildPublicUrl("/api/v1/integrations/github/oauth-callback");

    private string BuildPublicUrl(string path) => $"{PublicBaseUrl.TrimEnd('/')}{path}";
}
