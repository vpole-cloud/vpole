using Microsoft.Extensions.Configuration;
using Xunit;

namespace Pole.Tests.E2E.Infrastructure;

public sealed class E2ETestSettingsTests
{
    [Fact]
    public void DisabledSourceBuilds_DoesNotRequireLocalInfrastructureSettings()
    {
        var settings = E2ETestSettings.FromConfiguration(BuildConfiguration(
            [KeyValuePair.Create<string, string?>("SourceBuilds:Enabled", "false")]));

        Assert.Null(settings.SourceBuilds);
    }

    [Fact]
    public void EnabledSourceBuilds_UsesSafeDefaultsForOptionalSettings()
    {
        var settings = E2ETestSettings.FromConfiguration(BuildConfiguration(
        [
            KeyValuePair.Create<string, string?>("SourceBuilds:Enabled", "true"),
            KeyValuePair.Create<string, string?>("SourceBuilds:HarborComposeDirectory", "C:\\tools\\harbor"),
            KeyValuePair.Create<string, string?>("SourceBuilds:HarborAdminPassword", "local-password")
        ]));

        Assert.NotNull(settings.SourceBuilds);
        Assert.Equal("http://localhost:5000", settings.SourceBuilds.HarborBaseUrl);
        Assert.Equal("host.docker.internal:5000", settings.SourceBuilds.HarborRegistryHost);
        Assert.Equal("buildctl", settings.SourceBuilds.BuildctlPath);
    }

    [Fact]
    public void EnabledSourceBuilds_RequiresTheHarborComposeDirectory()
    {
        var configuration = BuildConfiguration(
        [
            KeyValuePair.Create<string, string?>("SourceBuilds:Enabled", "true"),
            KeyValuePair.Create<string, string?>("SourceBuilds:HarborAdminPassword", "local-password")
        ]);

        var exception = Assert.Throws<InvalidOperationException>(() => E2ETestSettings.FromConfiguration(configuration));

        Assert.Contains("SourceBuilds:HarborComposeDirectory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledSourceBuilds_RequiresTheHarborAdminPassword()
    {
        var configuration = BuildConfiguration(
        [
            KeyValuePair.Create<string, string?>("SourceBuilds:Enabled", "true"),
            KeyValuePair.Create<string, string?>("SourceBuilds:HarborComposeDirectory", "C:\\tools\\harbor")
        ]);

        var exception = Assert.Throws<InvalidOperationException>(() => E2ETestSettings.FromConfiguration(configuration));

        Assert.Contains("SourceBuilds:HarborAdminPassword", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration BuildConfiguration(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
