using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class StorageBrowserE2ETests(PoleE2EFixture fixture)
{
    [Fact]
    public async Task Platform_administrator_reads_target_inventory_and_creates_and_updates_a_revisioned_profile()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = budget.Token;
        await using var resources = new TestResourceScope("storage browser E2E");
        await using var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
        await browserContainer.StartAsync(ct);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint, new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 }, IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
        await page.GetByLabel("Username or email").FillAsync(fixture.PlatformAdminUser.Email);
        await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true }).FillAsync(fixture.PlatformAdminUser.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Storage", Exact = true }).ClickAsync();
        var target = page.GetByLabel("Deployment target", new PageGetByLabelOptions { Exact = true });
        await Assertions.Expect(page.GetByLabel("Display name", new PageGetByLabelOptions { Exact = true })).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        var targetId = await target.InputValueAsync();
        Assert.False(string.IsNullOrWhiteSpace(targetId));
        var name = $"UI volume {Guid.NewGuid():N}"[..22];
        await page.GetByLabel("Display name", new PageGetByLabelOptions { Exact = true }).FillAsync(name);
        var storageClass = await page.GetByLabel("Storage class", new PageGetByLabelOptions { Exact = true }).InputValueAsync();
        Assert.False(string.IsNullOrWhiteSpace(storageClass));
        var created = await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create profile", Exact = true }).ClickAsync(),
            response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/api/v1/platform/storage/profiles");
        Assert.True(created.Ok, $"Storage profile creation returned HTTP {created.Status}.");
        var profile = await created.JsonAsync() ?? throw new InvalidOperationException("Created storage profile was empty.");
        var id = Text(profile, "id");
        resources.Defer("disable browser storage profile", token => DisableProfileAsync(id, token));
        var mapping = Assert.Single(profile.GetProperty("targetMappings").EnumerateArray());
        Assert.Equal(targetId, Text(mapping, "deploymentTargetId"));
        Assert.Equal(storageClass, Text(mapping, "storageClassName"));
        var row = page.GetByTestId($"storage-profile-row-{id}");
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Edit", Exact = true }).ClickAsync();
        await page.GetByLabel("Description", new PageGetByLabelOptions { Exact = true }).FillAsync("Verified against target inventory");
        var updated = await page.RunAndWaitForResponseAsync(
            () => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save changes", Exact = true }).ClickAsync(),
            response => response.Request.Method == "PUT" && new Uri(response.Url).AbsolutePath == $"/api/v1/platform/storage/profiles/{id}");
        Assert.True(updated.Ok, $"Storage profile update returned HTTP {updated.Status}.");
        using var body = JsonDocument.Parse(updated.Request.PostData!);
        Assert.Equal(profile.GetProperty("revision").GetInt64(), body.RootElement.GetProperty("revision").GetInt64());
        Assert.False(body.RootElement.TryGetProperty("storageClassNames", out _));
        await Assertions.Expect(page.GetByLabel("Description", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync("Verified against target inventory");
        await CaptureAsync(page);
    }

    private async Task DisableProfileAsync(string id, CancellationToken ct)
    {
        using var read = fixture.CreatePlatformAdminRequest(HttpMethod.Get, "/api/v1/platform/storage/profiles");
        using var response = await fixture.Api.SendAsync(read, ct);
        Assert.True(response.IsSuccessStatusCode, $"Storage cleanup read returned HTTP {(int)response.StatusCode}.");
        var profiles = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var current = profiles.EnumerateArray().Single(item => Text(item, "id") == id);
        using var update = fixture.CreatePlatformAdminRequest(HttpMethod.Put, $"/api/v1/platform/storage/profiles/{id}");
        update.Content = JsonContent.Create(new
        {
            displayName = Text(current, "displayName"),
            description = Text(current, "description"),
            mediaType = Text(current, "mediaType"),
            performanceTier = Text(current, "performanceTier"),
            isEnabled = false,
            isDefault = false,
            targetMappings = current.GetProperty("targetMappings"),
            allowedResourceTypes = current.GetProperty("allowedResourceTypes"),
            tenantAccess = current.GetProperty("tenantAccess"),
            sizeLimits = current.GetProperty("sizeLimits"),
            revision = current.GetProperty("revision").GetInt64()
        });
        using var disabled = await fixture.Api.SendAsync(update, ct);
        Assert.True(disabled.IsSuccessStatusCode, $"Storage cleanup update returned HTTP {(int)disabled.StatusCode}.");
    }
    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString()!;
    private static async Task CaptureAsync(IPage page)
    {
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "frontend-storage"));
        Directory.CreateDirectory(directory);
        foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var theme in new[] { "light", "dark" })
            {
                if (await page.Locator("html").GetAttributeAsync("data-theme") != theme)
                    await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Switch to {theme} theme", Exact = true }).ClickAsync();
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, $"storage-{viewport}-{theme}.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), "Storage settings must fit its viewport.");
            }
        }
    }
}
