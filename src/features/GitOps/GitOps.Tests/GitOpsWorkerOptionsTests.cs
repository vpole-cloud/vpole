using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pole.Features.GitOps.Application;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class GitOpsWorkerOptionsTests
{
    [Theory]
    [InlineData("MaximumConsecutiveFailures", "6")]
    [InlineData("MaximumConsecutiveFailures", "0")]
    [InlineData("HeartbeatSeconds", "60")]
    public void InvalidRecoverySettingsAreRejectedByFeatureRegistration(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"GitOpsWorker:{key}"] = value
        }).Build();
        using var provider = new ServiceCollection().AddSingleton<IConfiguration>(configuration)
            .AddGitOpsFeature().BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<GitOpsWorkerOptions>>().Value);
    }

    [Fact]
    public void DefaultRecoverySettingsMatchTheRepositoryExecutionContract()
    {
        using var provider = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddGitOpsFeature().BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GitOpsWorkerOptions>>().Value;
        Assert.Equal(1, options.ScanIntervalMinutes);
        Assert.Equal(100, options.BatchSize);
        Assert.Equal(120, options.LeaseSeconds);
        Assert.Equal(20, options.HeartbeatSeconds);
        Assert.Equal(5, options.MaximumConsecutiveFailures);
    }
}
