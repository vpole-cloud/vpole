using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pole.Features.Quotas.Application;
using Xunit;

namespace Pole.Tests.Integration.Quotas;

public sealed class QuotaSettingsTests
{
    [Fact]
    public async Task InvalidQuotaSettingsPreventHostStartup()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["kubernetes:NamespaceNamePrefix"] = "test-pole",
                ["Quotas:ActiveReservationMinutes"] = "0",
                ["Quotas:FinalizedReservationHistoryDays"] = "-1"
            })
            .Build();
        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddQuotasFeature(configuration))
            .Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
    }
}
