using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pole.Features.Tenants.Application;
using Pole.Features.Tenants.Application.Invitations;
using Xunit;

namespace Pole.Tests.Integration.Tenants;

public sealed class InvitationSmtpSettingsTests
{
    private const string Prefix = "Invitations:Smtp:";

    [Theory]
    [InlineData("Host", "")]
    [InlineData("Host", "smtp://mail.example.test")]
    [InlineData("From", "invalid")]
    [InlineData("From", "")]
    [InlineData("Port", "0")]
    [InlineData("Port", "65536")]
    [InlineData("SendTimeout", "00:00:00")]
    [InlineData("SendTimeout", "00:05:01")]
    [InlineData("Username", "sender")]
    [InlineData("Password", "sensitive-test-value")]
    public async Task InvalidSettingStopsHostBeforeDelivery(string field, string value)
    {
        var settings = ValidSettings();
        settings[Prefix + field] = value;
        using var host = BuildHost(settings);
        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(Prefix + field, failure.Message);
        Assert.DoesNotContain("sensitive-test-value", failure.Message);
    }

    [Fact]
    public async Task MissingSectionReportsBothRequiredFields()
    {
        using var host = BuildHost([]);
        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(Prefix + "Host", failure.Message);
        Assert.Contains(Prefix + "From", failure.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidRelayStartsWithoutContactingTheServer(bool authenticated)
    {
        var settings = ValidSettings();
        if (authenticated)
        {
            settings[Prefix + "Username"] = "sender";
            settings[Prefix + "Password"] = "test-password";
        }

        using var host = BuildHost(settings);
        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task CustomSenderStartsWithoutSmtpConfiguration()
    {
        using var host = BuildHost(
            [],
            services => services.AddSingleton<SendInvitationMail>((_, _) => Task.CompletedTask));
        await host.StartAsync();
        await host.StopAsync();
    }

    private static Dictionary<string, string?> ValidSettings() => new()
    {
        [Prefix + "Host"] = "mail.example.test",
        [Prefix + "From"] = "pole@example.test",
    };

    private static IHost
        BuildHost(Dictionary<string, string?> settings, Action<IServiceCollection>? configure = null) =>
        new HostBuilder().ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(settings))
            .ConfigureServices(services =>
            {
                configure?.Invoke(services);
                services.AddTenantsFeature();
            })
            .Build();
}
