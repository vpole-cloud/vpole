using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pole.Features.GitProviders.Application;
using Pole.Features.GitProviders.Application.Common;
using Xunit;

namespace Pole.Features.GitProviders.Tests;

public sealed class GitHubWebhookOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EnabledGitHubRequiresASeparateWebhookSecret(string? secret)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["GitHubApp:Enabled"] = "true",
                    ["GitHubWebhook:Secret"] = secret,
                })
            .Build();
        using var provider = new ServiceCollection().AddGitProvidersFeature(configuration).BuildServiceProvider();
        var error = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<GitHubWebhookOptions>>().Value);
        Assert.Contains(error.Failures, value => value.Contains("GitHubWebhook:Secret", StringComparison.Ordinal));
    }

    [Fact]
    public void GitHubDisabledDeploymentsDoNotRequireAWebhookSecret()
    {
        using var provider = new ServiceCollection().AddGitProvidersFeature(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        Assert.Empty(provider.GetRequiredService<IOptions<GitHubWebhookOptions>>().Value.Secret);
    }
}
