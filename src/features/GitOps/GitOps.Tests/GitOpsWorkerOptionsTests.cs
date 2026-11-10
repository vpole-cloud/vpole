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
    [InlineData("CleanupCheckSeconds", "0")]
    [InlineData("CleanupCheckSeconds", "301")]
    public void InvalidRecoverySettingsAreRejectedByFeatureRegistration(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"GitOpsWorker:{key}"] = value,
                })
            .Build();
        using var provider = new ServiceCollection().AddSingleton<IConfiguration>(configuration)
            .AddGitOpsFeature()
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<GitOpsWorkerOptions>>().Value);
    }

    [Fact]
    public void DefaultRecoverySettingsMatchTheRepositoryExecutionContract()
    {
        using var provider = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddGitOpsFeature()
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GitOpsWorkerOptions>>().Value;
        Assert.Equal(1, options.ScanIntervalMinutes);
        Assert.Equal(100, options.BatchSize);
        Assert.Equal(120, options.LeaseSeconds);
        Assert.Equal(20, options.HeartbeatSeconds);
        Assert.Equal(5, options.CleanupCheckSeconds);
        Assert.Equal(5, options.MaximumConsecutiveFailures);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void Cleanup_check_cadence_is_independent_of_lease_heartbeat(int seconds)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["GitOpsWorker:CleanupCheckSeconds"] = seconds.ToString(),
                })
            .Build();
        using var provider = new ServiceCollection().AddSingleton<IConfiguration>(configuration)
            .AddGitOpsFeature()
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GitOpsWorkerOptions>>().Value;

        Assert.Equal(seconds, options.CleanupCheckSeconds);
        Assert.Equal(20, options.HeartbeatSeconds);
        Assert.Equal(120, options.LeaseSeconds);
    }
}
