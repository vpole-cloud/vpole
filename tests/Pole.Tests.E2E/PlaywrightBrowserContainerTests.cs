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
        const string address = "http://browser-isolation.pole.test/";
        var firstRoutes = 0;

        await using (var browser = await playwright.Chromium.ConnectAsync(container.WebSocketEndpoint))
        await using (var context = await browser.NewContextAsync())
        {
            var page = await context.NewPageAsync();
            await page.SetContentAsync("<title>Browser transport ready</title>");
            Assert.Equal("Browser transport ready", await page.TitleAsync());
            await context.RouteAsync(
                address,
                async route =>
                {
                    firstRoutes++;
                    await route.FulfillAsync(
                        new RouteFulfillOptions { ContentType = "text/html", Body = "<title>First journey</title>" });
                });
            await page.GotoAsync(address);
            await context.AddCookiesAsync([new Cookie { Name = "journey", Value = "first", Url = address }]);
            await page.EvaluateAsync("localStorage.setItem('journey', 'first')");
            Assert.Single(await context.CookiesAsync());
            Assert.Equal(1, firstRoutes);
        }

        // A new remote connection and context must not inherit authentication, storage or routes.
        await using (var browser = await playwright.Chromium.ConnectAsync(container.WebSocketEndpoint))
        await using (var context = await browser.NewContextAsync())
        {
            Assert.Empty(await context.CookiesAsync());
            await context.RouteAsync(
                address,
                route => route.FulfillAsync(
                    new RouteFulfillOptions
                    {
                        ContentType = "text/html",
                        Body = "<title>Second journey</title>",
                    }));
            var page = await context.NewPageAsync();
            await page.GotoAsync(address);
            Assert.Equal("Second journey", await page.TitleAsync());
            Assert.Null(await page.EvaluateAsync<string?>("localStorage.getItem('journey')"));
            Assert.Equal(1, firstRoutes);
        }
    }
}
