using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pole.Features.Postgres.Application;
using Pole.Features.Postgres.Application.Common;
using Xunit;

namespace Pole.Features.Postgres.Tests;

public sealed class PostgresSettingsTests
{
    [Theory]
    [InlineData("SupportedVersions:From", "0")]
    [InlineData("SupportedVersions:To", "15")]
    [InlineData("DefaultDeploymentMode", "7")]
    [InlineData("OperatorImage:Server", " ")]
    [InlineData("StatefulSetImage:Name", "")]
    public void InvalidSettingsFailWhenResolved(string key, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { [$"Postgres:{key}"] = value })
            .Build();
        using var services = new ServiceCollection().AddPostgresFeature(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<PostgresSettings>());
    }
}
