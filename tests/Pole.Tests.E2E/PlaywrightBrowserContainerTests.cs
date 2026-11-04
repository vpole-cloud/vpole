using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class PlaywrightBrowserContainerTests
{
    [Fact]
    public async Task Start_waits_until_published_endpoint_accepts_browser_connections()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var container = new PlaywrightBrowserContainer(TestRunIdentity.Create());
        await container.StartAsync(cancellation.Token);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectAsync(container.WebSocketEndpoint);
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync("<title>Browser transport ready</title>");

        Assert.Equal("Browser transport ready", await page.TitleAsync());
    }
}
