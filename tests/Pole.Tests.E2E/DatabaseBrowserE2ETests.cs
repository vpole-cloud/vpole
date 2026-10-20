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
        var resources = new TestResourceScope("PostgreSQL browser E2E");
        await resources.RunAsync(async () =>
        {
            var name = $"pg-{Guid.NewGuid():N}"[..16];
            var group = await CreateGroupAsync(name, timeout.Token);
            resources.Defer("delete PostgreSQL browser group", token => DeleteGroupAsync(group, token));
            var placement = await PrepareStorageProfileAsync("pole/postgresql", timeout.Token);
            resources.Defer("disable database storage profile", token => PlatformTestProfiles.DisableStorageAsync(fixture, placement.ProfileId, token));
            var profile = await CreateComputeProfileAsync(placement.TargetId, timeout.Token);
            var profileId = profile.GetProperty("id").GetString()!;
            resources.Defer("delete database compute profile", token => DeleteComputeProfileAsync(profileId, profile.GetProperty("revision").GetInt64(), token));
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
            await CaptureAsync(page, "postgres", "catalog", "catalog", "PostgreSQL databases");
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "New PostgreSQL", Exact = true }).ClickAsync();
            await content.GetByLabel("Resource group").SelectOptionAsync(group);
            await content.GetByLabel("Deployment target").SelectOptionAsync(placement.TargetId);
            await content.GetByLabel("Storage profile").SelectOptionAsync(placement.ProfileId);
            var create = content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create PostgreSQL", Exact = true });
            try { await Assertions.Expect(create).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 20_000 }); }
            catch
            {
                // This is before entering credentials; the form contains metadata only.
                await CaptureAsync(page, "postgres", "create-unavailable", "create", "Create PostgreSQL database");
                output.WriteLine("Creation form prerequisite messages: " + await content.InnerTextAsync());
                throw;
            }
            Assert.True(await content.GetByLabel("Instances", new LocatorGetByLabelOptions { Exact = true }).IsDisabledAsync());
            await content.GetByLabel("Name", new LocatorGetByLabelOptions { Exact = true }).FillAsync(name);
            // Capture before entering either credential, including the username.
            await CaptureAsync(page, "postgres", "create", "create", "Create PostgreSQL database");
            await content.GetByLabel("Database username", new LocatorGetByLabelOptions { Exact = true }).FillAsync("app");
            await content.GetByLabel("Database password", new LocatorGetByLabelOptions { Exact = true }).FillAsync($"test-{Guid.NewGuid():N}");
            var accepted = await page.RunAndWaitForResponseAsync(() => create.ClickAsync(), response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath.TrimEnd('/') == "/api/v1/postgres");
            Assert.Equal(202, accepted.Status);
            var created = await accepted.JsonAsync() ?? throw new InvalidOperationException("PostgreSQL create response is missing.");
            var id = created.GetProperty("id").GetString()!;
            resources.Defer("delete browser PostgreSQL", token => DeleteDatabaseAsync("postgres", id, token));
            await fixture.WaitForNamespaceCreationAsync(group, timeout.Token);
            await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = name, Exact = true }).WaitForAsync();
            await CaptureAsync(page, "postgres", "overview-pending", id, name);
            await WaitForAppliedAsync("postgres", id, timeout.Token);
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Refresh database", Exact = true }).ClickAsync();
            await Assertions.Expect(content.GetByText("postgresql · TCP", new LocatorGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await CaptureAsync(page, "postgres", "overview", id, name);
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
            Assert.True(await content.GetByLabel("Storage per instance (GiB)", new LocatorGetByLabelOptions { Exact = true }).IsDisabledAsync());
            var compute = content.GetByLabel("Compute profile");
            // Select our known non-default profile rather than relying on option order.
            using (var currentRead = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/postgres/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId))
            using (var currentResponse = await fixture.Api.SendAsync(currentRead, timeout.Token))
            {
                currentResponse.EnsureSuccessStatusCode();
                var currentDatabase = await currentResponse.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                Assert.NotEqual(profileId, currentDatabase.GetProperty("placement").GetProperty("compute").GetProperty("profile").GetProperty("computeProfileId").GetString());
            }
            await Assertions.Expect(compute.Locator($"option[value='{profileId}']")).ToHaveCountAsync(1);
            await compute.SelectOptionAsync(profileId);
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Overview", Exact = true }).ClickAsync();
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
            Assert.Equal(profileId, await compute.InputValueAsync());
            await CaptureAsync(page, "postgres", "configuration", id, "Database configuration");
            var updated = await page.RunAndWaitForResponseAsync(() => content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save changes", Exact = true }).ClickAsync(),
                response => response.Request.Method == "PUT" && new Uri(response.Url).AbsolutePath.EndsWith($"/postgres/{id}", StringComparison.Ordinal));
            Assert.Equal(202, updated.Status);
            using (var read = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/postgres/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId))
            using (var persisted = await fixture.Api.SendAsync(read, timeout.Token))
            {
                persisted.EnsureSuccessStatusCode();
                var database = await persisted.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
                Assert.Equal(profileId, database.GetProperty("placement").GetProperty("compute").GetProperty("profile").GetProperty("computeProfileId").GetString());
                Assert.Equal(1, database.GetProperty("spec").GetProperty("instances").GetInt32());
                Assert.Equal(1, database.GetProperty("spec").GetProperty("storage").GetInt32());
            }
            await page.ReloadAsync();
            await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Database configuration", Exact = true }).WaitForAsync();
            Assert.Equal("1", await content.GetByLabel("Storage per instance (GiB)", new LocatorGetByLabelOptions { Exact = true }).InputValueAsync());
            await WaitForAppliedAsync("postgres", id, timeout.Token);
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Credentials", Exact = true }).ClickAsync();
            await CaptureAsync(page, "postgres", "credentials", id, name);
            // Native rotation is unsupported, independent of deployment reconciliation.
            Assert.Equal(0, await content.Locator("input[type=password]:visible").CountAsync());
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Overview", Exact = true }).ClickAsync();
        });
    }

    [Fact]
    public async Task User_can_provision_edit_and_delete_Redis_with_readable_single_target_placement()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var resources = new TestResourceScope("Redis browser E2E");
        await resources.RunAsync(async () =>
        {
            var name = $"redis-{Guid.NewGuid():N}"[..18];
            var group = await CreateGroupAsync(name, timeout.Token);
            resources.Defer("delete Redis browser group", token => DeleteGroupAsync(group, token));
            var placement = await PrepareStorageProfileAsync("pole/redis", timeout.Token);
            resources.Defer("disable database storage profile", token => PlatformTestProfiles.DisableStorageAsync(fixture, placement.ProfileId, token));
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
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Redis", Exact = true }).ClickAsync();
            await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Redis services", Exact = true }).WaitForAsync();
            await CaptureAsync(page, "redis", "catalog", "catalog", "Redis services");
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "New Redis", Exact = true }).ClickAsync();
            await content.GetByLabel("Resource group").SelectOptionAsync(group);
            await content.GetByLabel("Deployment target").SelectOptionAsync(placement.TargetId);
            await content.GetByLabel("Storage profile").SelectOptionAsync(placement.ProfileId);
            await content.GetByLabel("Name").FillAsync(name);
            await content.GetByLabel("Tags").FillAsync("e2e, cache");
            var create = content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create Redis", Exact = true });
            await Assertions.Expect(create).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 20_000 });
            await CaptureAsync(page, "redis", "create", "create", "Create Redis service");
            var accepted = await page.RunAndWaitForResponseAsync(() => create.ClickAsync(), response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath.TrimEnd('/') == "/api/v1/redis-services");
            Assert.Equal(202, accepted.Status);
            var created = await accepted.JsonAsync() ?? throw new InvalidOperationException("Redis response is missing.");
            var id = created.GetProperty("redisId").GetString()!;
            resources.Defer("delete browser Redis", token => DeleteDatabaseAsync("redis-services", id, token));
            await fixture.WaitForNamespaceCreationAsync(group, timeout.Token);
            await WaitForAppliedAsync("redis-services", id, timeout.Token);
            await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = name, Exact = true }).WaitForAsync();
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Refresh Redis", Exact = true }).ClickAsync();
            await Assertions.Expect(content.GetByText("redis · TCP", new LocatorGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await CaptureAsync(page, "redis", "overview", id, name);
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
            Assert.True(await content.GetByLabel("Storage (GiB)", new LocatorGetByLabelOptions { Exact = true }).IsDisabledAsync());
            await content.GetByLabel("Tags").FillAsync("e2e, updated");
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Overview", Exact = true }).ClickAsync();
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
            Assert.Contains("updated", await content.GetByLabel("Tags").InputValueAsync());
            await CaptureAsync(page, "redis", "configuration", id, "Redis configuration");
            var updated = await page.RunAndWaitForResponseAsync(() => content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Save changes", Exact = true }).ClickAsync(),
                response => response.Request.Method == "PUT" && new Uri(response.Url).AbsolutePath.EndsWith($"/redis-services/{id}", StringComparison.Ordinal));
            Assert.Equal(202, updated.Status);
            await page.ReloadAsync();
            await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Redis configuration", Exact = true }).WaitForAsync();
            Assert.Equal("1", await content.GetByLabel("Storage (GiB)", new LocatorGetByLabelOptions { Exact = true }).InputValueAsync());
            Assert.Contains("updated", await content.GetByLabel("Tags").InputValueAsync());
            await WaitForAppliedAsync("redis-services", id, timeout.Token);
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete Redis", Exact = true }).ClickAsync();
            var deleted = await page.RunAndWaitForResponseAsync(() => page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete Redis", Exact = true }).ClickAsync(),
                response => response.Request.Method == "DELETE" && new Uri(response.Url).AbsolutePath.EndsWith($"/redis-services/{id}", StringComparison.Ordinal));
            Assert.Equal(202, deleted.Status);
        });
    }

    private async Task LoginAsync(IPage page)
    {
        await page.GotoAsync(fixture.FrontendBaseAddress.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
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

    private async Task<(string TargetId, string ProfileId)> PrepareStorageProfileAsync(string resourceType, CancellationToken token)
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
        var profile = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        return (targetId, profile.GetProperty("id").GetString()!);
    }

    private async Task<JsonElement> CreateComputeProfileAsync(string targetId, CancellationToken ct)
    {
        using var request = fixture.CreatePlatformAdminRequest(HttpMethod.Post, "/api/v1/platform/compute-profiles/");
        request.Content = JsonContent.Create(new
        {
            displayName = $"Database edit {Guid.NewGuid():N}",
            description = "Owned browser configuration test profile",
            resources = new { requests = new { cpuMillis = 50, memoryMi = 128 }, limits = new { cpuMillis = 250, memoryMi = 256 } },
            architecture = "Any",
            extendedResources = Array.Empty<object>(),
            allowedResourceTypes = new[] { "pole/postgresql" },
            tenantAccess = new { kind = "selected-tenants", tenantIds = new[] { fixture.Alpha.DefaultTenantId } },
            targetMappings = new[] { new { deploymentTargetId = targetId, nodeSelector = Array.Empty<object>(), requiredNodeAffinity = Array.Empty<object>(), preferredNodeAffinity = Array.Empty<object>(), tolerations = Array.Empty<object>(), runtimeClassName = (string?)null } },
            isEnabled = true,
            isDefault = false
        });
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, $"Compute profile creation returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task DeleteComputeProfileAsync(string id, long revision, CancellationToken ct)
    {
        using var request = fixture.CreatePlatformAdminRequest(HttpMethod.Delete, $"/api/v1/platform/compute-profiles/{id}?revision={revision}");
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound, $"Compute cleanup returned HTTP {(int)response.StatusCode}.");
    }

    private Task WaitForAppliedAsync(string route, string id, CancellationToken token)
    {
        string? last = null;
        return TestWait.UntilAsync(async probeToken =>
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/{route}/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, probeToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(probeToken);
            var placement = result.GetProperty("placement");
            var diagnostic = $"status={placement.GetProperty("status").GetString()}, failureCode={placement.GetProperty("failureCode").GetString()}";
            if (diagnostic != last) { output.WriteLine("Database reconciliation: " + diagnostic); last = diagnostic; }
            return placement.GetProperty("status").GetString() == "Applied" && placement.GetProperty("desiredGeneration").GetInt64() == placement.GetProperty("observedGeneration").GetInt64();
        }, TimeSpan.FromMinutes(3), "Database reconciliation did not apply the current generation.", token, pollInterval: TimeSpan.FromSeconds(2));
    }

    private async Task DeleteDatabaseAsync(string route, string id, CancellationToken token)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromMinutes(1));
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/v1/{route}/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, bounded.Token);
        if (TestDeletion.IsComplete(response.StatusCode)) return;
        await TestWait.UntilAsync(async probeToken =>
        {
            using var read = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/{route}/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var remaining = await fixture.Api.SendAsync(read, probeToken);
            return TestDeletion.IsComplete(remaining.StatusCode);
        }, TimeSpan.FromMinutes(1), "The browser database did not finish deletion.", bounded.Token, pollInterval: TimeSpan.FromSeconds(1));
    }

    private async Task DeleteGroupAsync(string id, CancellationToken token)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/v1/resource-groups/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, token);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);
        await fixture.WaitForNamespaceDeletionAsync(id, token);
    }

    private async Task CaptureAsync(IPage page, string resource, string section, string resourceId, string heading)
    {
        await BrowserWorkspaceReadiness.AssertAsync(page, resourceId, heading);
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "frontend-databases"));
        Directory.CreateDirectory(directory);
        foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var theme in new[] { "light", "dark" })
            {
                if (await page.Locator("html").GetAttributeAsync("data-theme") != theme)
                    await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Switch to {theme} theme", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
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
