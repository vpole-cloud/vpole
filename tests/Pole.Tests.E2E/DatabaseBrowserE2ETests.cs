using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class DatabaseBrowserE2ETests(PoleE2EFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task User_can_provision_and_edit_PostgreSQL_with_transient_credentials_and_readable_placement()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await using var resources = new TestResourceScope("PostgreSQL browser E2E");
        var name = $"pg-{Guid.NewGuid():N}"[..16];
        var group = await CreateGroupAsync(name, timeout.Token);
        resources.Defer("delete PostgreSQL browser group", token => DeleteGroupAsync(group, token));
        await PrepareStorageProfileAsync("pole/postgresql", timeout.Token);
        await using var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
        await browserContainer.StartAsync(timeout.Token);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint,
            new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            IgnoreHTTPSErrors = true
        });
        var page = await context.NewPageAsync();
        await LoginAsync(page);
        var content = page.Locator("#app-content");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "PostgreSQL", Exact = true }).ClickAsync();
        await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "PostgreSQL databases", Exact = true }).WaitForAsync();
        await CaptureAsync(page, "postgres", "catalog");
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "New PostgreSQL", Exact = true }).ClickAsync();
        await content.GetByLabel("Resource group").SelectOptionAsync(group);
        var create = content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create PostgreSQL", Exact = true });
        try { await Assertions.Expect(create).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 20_000 }); }
        catch
        {
            // This is before entering credentials; the form contains metadata only.
            await CaptureAsync(page, "postgres", "create-unavailable");
            output.WriteLine("Creation form prerequisite messages: " + await content.InnerTextAsync());
            throw;
        }
        Assert.True(await content.GetByLabel("Instances", new LocatorGetByLabelOptions { Exact = true }).IsDisabledAsync());
        await content.GetByLabel("Name", new LocatorGetByLabelOptions { Exact = true }).FillAsync(name);
        // Capture before entering either credential, including the username.
        await CaptureAsync(page, "postgres", "create");
        await content.GetByLabel("Database username", new LocatorGetByLabelOptions { Exact = true }).FillAsync("app");
        await content.GetByLabel("Database password", new LocatorGetByLabelOptions { Exact = true }).FillAsync($"test-{Guid.NewGuid():N}");
        var accepted = await page.RunAndWaitForResponseAsync(() => create.ClickAsync(), response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath.TrimEnd('/') == "/api/v1/postgres");
        Assert.Equal(202, accepted.Status);
        var created = await accepted.JsonAsync() ?? throw new InvalidOperationException("PostgreSQL create response is missing.");
        var id = created.GetProperty("id").GetString()!;
        resources.Defer("delete browser PostgreSQL", token => DeleteDatabaseAsync("postgres", id, token));
        await fixture.WaitForNamespaceCreationAsync(group, timeout.Token);
        await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = name, Exact = true }).WaitForAsync();
        await CaptureAsync(page, "postgres", "overview-pending");
        await WaitForAppliedAsync("postgres", id, timeout.Token);
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Refresh database", Exact = true }).ClickAsync();
        await CaptureAsync(page, "postgres", "overview");
        await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
        Assert.True(await content.GetByLabel("Storage per instance (GiB)", new LocatorGetByLabelOptions { Exact = true }).IsDisabledAsync());
        var compute = content.GetByLabel("Compute profile");
        var profile = await compute.Locator("option").Nth(1).GetAttributeAsync("value");
        await compute.SelectOptionAsync(profile!);
        await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Overview", Exact = true }).ClickAsync();
        await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
        Assert.Equal(profile, await compute.InputValueAsync());
        await CaptureAsync(page, "postgres", "configuration");
        var updated = await page.RunAndWaitForResponseAsync(() => content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save changes", Exact = true }).ClickAsync(),
            response => response.Request.Method == "PUT" && new Uri(response.Url).AbsolutePath.EndsWith($"/postgres/{id}", StringComparison.Ordinal));
        Assert.Equal(202, updated.Status);
        await page.ReloadAsync();
        await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Database configuration", Exact = true }).WaitForAsync();
        Assert.Equal("1", await content.GetByLabel("Storage per instance (GiB)", new LocatorGetByLabelOptions { Exact = true }).InputValueAsync());
        await WaitForAppliedAsync("postgres", id, timeout.Token);
        await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Credentials", Exact = true }).ClickAsync();
        await CaptureAsync(page, "postgres", "credentials");
        // Native rotation is unsupported, independent of deployment reconciliation.
        Assert.Equal(0, await content.Locator("input[type=password]:visible").CountAsync());
        await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Overview", Exact = true }).ClickAsync();
    }

    private async Task LoginAsync(IPage page)
    {
        await page.GotoAsync(fixture.FrontendBaseAddress.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
        await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
        await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true }).FillAsync(fixture.Alpha.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
        await page.GetByLabel("Tenant").SelectOptionAsync(fixture.Alpha.DefaultTenantId);
    }

    private async Task<string> CreateGroupAsync(string name, CancellationToken token)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/resource-groups", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        request.Content = JsonContent.Create(new { name, description = "Database browser test", tags = new[] { "e2e" } });
        using var response = await fixture.Api.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return result.GetProperty("id").GetString()!;
    }

    private async Task PrepareStorageProfileAsync(string resourceType, CancellationToken token)
    {
        using var targetsRequest = fixture.CreatePlatformAdminRequest(HttpMethod.Get, "/api/v1/platform/deployment-targets/");
        using var targetsResponse = await fixture.Api.SendAsync(targetsRequest, token);
        targetsResponse.EnsureSuccessStatusCode();
        var targets = await targetsResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        var targetId = targets.EnumerateArray().Single(target => target.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetString()!;
        using var classesRequest = fixture.CreatePlatformAdminRequest(HttpMethod.Get, $"/api/v1/platform/storage/storage-classes?deploymentTargetId={targetId}");
        using var classesResponse = await fixture.Api.SendAsync(classesRequest, token);
        classesResponse.EnsureSuccessStatusCode();
        var classes = await classesResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        var storageClassName = classes.EnumerateArray().First().GetProperty("name").GetString()!;
        using var request = fixture.CreatePlatformAdminRequest(HttpMethod.Post, "/api/v1/platform/storage/profiles");
        request.Content = JsonContent.Create(new
        {
            displayName = $"Browser test disk {resourceType}",
            description = "Ephemeral test profile",
            mediaType = "Network",
            performanceTier = "Standard",
            isEnabled = true,
            isDefault = false,
            targetMappings = new[] { new { deploymentTargetId = targetId, storageClassName } },
            allowedResourceTypes = new[] { resourceType },
            tenantAccess = new { mode = "SelectedTenants", tenantIds = new[] { fixture.Alpha.DefaultTenantId } }
        });
        using var response = await fixture.Api.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
    }

    private async Task WaitForAppliedAsync(string route, string id, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        string? last = null;
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/{route}/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            var placement = result.GetProperty("placement");
            var diagnostic = $"status={placement.GetProperty("status").GetString()}, failureCode={placement.GetProperty("failureCode").GetString()}";
            if (diagnostic != last) { output.WriteLine("Database reconciliation: " + diagnostic); last = diagnostic; }
            if (DateTime.UtcNow - started > TimeSpan.FromMinutes(3))
            {
                output.WriteLine("Database failure kinds: " + await fixture.GetDatabaseFailureKindsAsync(token));
                throw new InvalidOperationException($"Database reconciliation did not apply: status={placement.GetProperty("status").GetString()}, failureCode={placement.GetProperty("failureCode").GetString()}.");
            }
            if (placement.GetProperty("status").GetString() == "Applied" && placement.GetProperty("desiredGeneration").GetInt64() == placement.GetProperty("observedGeneration").GetInt64()) return;
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
    }

    private async Task DeleteDatabaseAsync(string route, string id, CancellationToken token)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromMinutes(1));
        token = bounded.Token;
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/v1/{route}/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, token);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);
        while (response.StatusCode != HttpStatusCode.NotFound)
        {
            using var read = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/{route}/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var remaining = await fixture.Api.SendAsync(read, token);
            if (remaining.StatusCode == HttpStatusCode.NotFound) return;
            remaining.EnsureSuccessStatusCode();
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
    }

    private async Task DeleteGroupAsync(string id, CancellationToken token)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/v1/resource-groups/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, token);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);
        await fixture.WaitForNamespaceDeletionAsync(id, token);
    }

    private async Task CaptureAsync(IPage page, string resource, string section)
    {
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "frontend-databases"));
        Directory.CreateDirectory(directory);
        foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var theme in new[] { "light", "dark" })
            {
                if (await page.Locator("html").GetAttributeAsync("data-theme") != theme)
                    await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Switch to {theme} theme", Exact = true }).ClickAsync();
                await page.EvaluateAsync("window.scrollTo(0, 0)");
                var screenshot = Path.Combine(directory, $"{resource}-{viewport}-{section}-{theme}.png");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshot, FullPage = true, Animations = ScreenshotAnimations.Disabled });
                output.WriteLine($"Database screenshot: {screenshot}");
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), $"Database workspace overflow: {viewport}/{section}/{theme}.");
            }
        }
        await page.SetViewportSizeAsync(1440, 900);
    }
}
