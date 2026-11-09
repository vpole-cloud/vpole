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
    private readonly TestTimings _timings = new(typeof(StorageBrowserE2ETests));

    [Fact]
    public async Task Platform_administrator_reads_target_inventory_and_creates_and_updates_a_revisioned_profile()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var setupTiming = _timings.Measure("e2e.setup");
        using var testEvidence = new TestPhaseJournal(nameof(Platform_administrator_reads_target_inventory_and_creates_and_updates_a_revisioned_profile)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = budget.Token;
        var resources = new TestResourceScope("storage browser E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            string browserEndpoint;
            using (_timings.Measure("e2e.browser.acquire"))
                browserEndpoint = await fixture.GetBrowserEndpointAsync(ct);
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(browserEndpoint, new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            setupTiming.Dispose();
            using var scenarioTiming = _timings.Measure("e2e.scenario");
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 }, IgnoreHTTPSErrors = true });
            await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
            var page = await BrowserEvidence.NewPageAsync(context);
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
            await page.GetByLabel("Technical name", new PageGetByLabelOptions { Exact = true })
                .FillAsync($"disk-{Guid.NewGuid():N}");
            await page.GetByLabel("Display name", new PageGetByLabelOptions { Exact = true }).FillAsync(name);
            var storageClass = await page.GetByLabel("Storage class", new PageGetByLabelOptions { Exact = true }).InputValueAsync();
            Assert.False(string.IsNullOrWhiteSpace(storageClass));
            var created = await page.RunAndWaitForResponseAsync(
                () => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create profile", Exact = true }).ClickAsync(),
                response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath == "/api/v1/platform/storage/profiles");
            Assert.True(created.Ok, $"Storage profile creation returned HTTP {created.Status}.");
            var profile = await created.JsonAsync() ?? throw new InvalidOperationException("Created storage profile was empty.");
            var id = Text(profile, "id");
            resources.Defer("disable browser storage profile", token => PlatformTestProfiles.DisableStorageAsync(fixture, id, token));
            var mapping = Assert.Single(profile.GetProperty("targetMappings").EnumerateArray());
            Assert.Equal(targetId, Text(mapping, "deploymentTargetId"));
            Assert.Equal(storageClass, Text(mapping, "storageClassName"));
            // A successful HTTP response precedes the UI's accepted-write refresh and form reset.
            await Assertions.Expect(page.GetByLabel("Display name", new PageGetByLabelOptions { Exact = true }))
                .ToHaveValueAsync("", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
            var row = page.GetByTestId($"storage-profile-row-{id}");
            await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Edit", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Edit storage profile", Exact = true })).ToBeVisibleAsync();
            await page.GetByLabel("Description", new PageGetByLabelOptions { Exact = true }).FillAsync("Verified against target inventory");
            var updated = await SaveProfileAsync(page, id);
            Assert.True(updated.Ok, $"Storage profile update returned HTTP {updated.Status}.");
            using var body = JsonDocument.Parse(updated.Request.PostData!);
            Assert.Equal(profile.GetProperty("revision").GetInt64(), body.RootElement.GetProperty("revision").GetInt64());
            Assert.False(body.RootElement.TryGetProperty("storageClassNames", out _));
            await Assertions.Expect(page.GetByLabel("Description", new PageGetByLabelOptions { Exact = true })).ToHaveValueAsync("Verified against target inventory");
            await CaptureAsync(page);
        });
    }

    private static async Task<IResponse> SaveProfileAsync(IPage page, string id)
    {
        var requests = 0;
        var failures = 0;
        bool IsUpdate(IRequest request) => request.Method == "PUT" &&
                                           new Uri(request.Url).AbsolutePath == $"/api/v1/platform/storage/profiles/{id}";
        page.Request += (_, request) =>
        {
            if (IsUpdate(request)) Interlocked.Increment(ref requests);
        };
        page.RequestFailed += (_, request) =>
        {
            if (IsUpdate(request)) Interlocked.Increment(ref failures);
        };
        try
        {
            return await page.RunAndWaitForResponseAsync(
                () => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save changes", Exact = true }).ClickAsync(),
                response => IsUpdate(response.Request));
        }
        catch (TimeoutException failure)
        {
            // Only fixed labels/counts leave diagnostics: never DOM text, request bodies, or credentials.
            var diagnostic = $"requests={Volatile.Read(ref requests)}; requestFailures={Volatile.Read(ref failures)}";
            try
            {
                var save = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Save changes", Exact = true });
                var form = page.Locator("form.resource-group-form");
                var invalid = await form.CountAsync() == 1
                    ? await form.EvaluateAsync<int>("form => Array.from(form.elements).filter(field => field.validity && !field.validity.valid).length").WaitAsync(TimeSpan.FromSeconds(5))
                    : -1;
                var buttons = await save.CountAsync();
                var enabled = buttons == 1 && await save.IsEnabledAsync(new LocatorIsEnabledOptions { Timeout = 5000 });
                diagnostic += $"; saveButtons={buttons}; saveEnabled={enabled}; invalidControls={invalid}; dialogs={await page.GetByRole(AriaRole.Dialog).CountAsync()}";
            }
            catch (Exception)
            {
                diagnostic += "; formState=unavailable";
            }
            throw new TimeoutException($"Storage profile update response timeout: {diagnostic}.", failure);
        }
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
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, $"storage-{viewport}-{theme}.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), "Storage settings must fit its viewport.");
            }
        }
    }
}
