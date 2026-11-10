namespace Pole.Features.GitProviders.Application.Common;

public sealed class GitHubWebhookOptions
{
    public const string SectionName = "GitHubWebhook";
    public const int MaximumBodyBytes = 25 * 1024 * 1024;
    public string Secret { get; init; } = "";
}
