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
public sealed class SecretBrowserE2ETests(PoleE2EFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task User_can_create_rotate_bind_inspect_and_detach_write_only_Secret_projections()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        await using var resources = new TestResourceScope("Secret browser E2E");
        var name = $"keys-{Guid.NewGuid():N}"[..18];
        var group = (await SendAsync(HttpMethod.Post, "/api/v1/resource-groups", new { name, description = "Secret browser test", tags = new[] { "e2e" } }, token)).GetProperty("id").GetString()!;
        resources.Defer("delete Secret browser group", cleanup => DeleteGroupAsync(group, cleanup));
        var consumerName = $"app-{Guid.NewGuid():N}"[..18];
        var consumer = (await SendAsync(HttpMethod.Post, "/api/v1/web-services", new
        {
            name = consumerName,
            resourceGroupId = group,
            exposure = "Public",
            tags = new[] { "e2e" },
            spec = new
            {
                image = new { server = "docker.io", name = "library/nginx", tag = "1.27" },
                config = new { environmentVariables = new Dictionary<string, string>(), portMappings = new[] { new { servicePort = 80, targetPort = "80", protocol = "TCP", name = "http" } } },
                autoscaling = (object?)null,
                probes = new { startup = Probe(), liveness = Probe(), readiness = Probe() }
            },
            placement = new { isolationScopeId = (string?)null, targets = new[] { new { deploymentTargetId = (string?)null, fixedReplicas = 1, computeProfileId = (string?)null } } }
        }, token)).GetProperty("webServiceId").GetString()!;
        resources.Defer("delete Secret consumer", cleanup => DeleteResourceAsync("web-services", consumer, cleanup));
        await fixture.WaitForNamespaceCreationAsync(group, token);
        var endpointPath = $"/api/v1/resource-connections/endpoints?resourceType=pole%2Fweb-service&resourceId={consumer}";
        var discovery = await SendAsync(HttpMethod.Get, endpointPath, null, token);
        var endpoint = Assert.Single(discovery.GetProperty("endpoints").EnumerateArray());
        Assert.Equal("http", endpoint.GetProperty("id").GetString());
        Assert.Equal("tcp", endpoint.GetProperty("protocol").GetString());
        Assert.Equal(80, endpoint.GetProperty("port").GetInt32());
        using (var unauthenticated = await fixture.Api.GetAsync(endpointPath, token))
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using (var foreignRequest = fixture.CreateAuthenticatedRequest(HttpMethod.Get, endpointPath, fixture.Beta, fixture.Beta.DefaultTenantId))
        using (var foreignResponse = await fixture.Api.SendAsync(foreignRequest, token))
            Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
        await using var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
        await browserContainer.StartAsync(token);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint, new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 }, IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
        await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
        await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true }).FillAsync(fixture.Alpha.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
        await page.GetByLabel("Tenant").SelectOptionAsync(fixture.Alpha.DefaultTenantId);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Secrets", Exact = true }).ClickAsync();
        var content = page.Locator("#app-content");
        await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Secrets", Exact = true }).WaitForAsync();
        await CaptureAsync(page, "catalog");
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "New Secret", Exact = true }).ClickAsync();
        await content.GetByLabel("Resource group").SelectOptionAsync(group);
        await content.GetByLabel("Name", new LocatorGetByLabelOptions { Exact = true }).FillAsync(name);
        await content.GetByLabel("Purpose", new LocatorGetByLabelOptions { Exact = true }).FillAsync("Application credential projection");
        await content.GetByLabel("Key 1", new LocatorGetByLabelOptions { Exact = true }).FillAsync("password");
        var create = content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create Secret", Exact = true });
        await Assertions.Expect(create).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 20_000 });
        // All screenshots are captured before values are entered, or after the sensitive form unmounts.
        await CaptureAsync(page, "create");
        await content.GetByLabel("Value 1").FillAsync($"test-{Guid.NewGuid():N}");
        var accepted = await page.RunAndWaitForResponseAsync(() => create.ClickAsync(), response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath.TrimEnd('/') == "/api/v1/secrets");
        Assert.Equal(201, accepted.Status);
        var secret = (await accepted.JsonAsync())!.Value.GetProperty("id").GetString()!;
        resources.Defer("detach and delete browser Secret", cleanup => CleanupSecretAsync(secret, cleanup));
        await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = name, Exact = true }).WaitForAsync();
        await CaptureAsync(page, "overview");
        await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Bindings", Exact = true }).ClickAsync();
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Add binding", Exact = true }).ClickAsync();
        await content.GetByLabel("Consuming resource").SelectOptionAsync(new SelectOptionValue { Label = $"{consumerName} · Web service" });
        var bind = content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create binding", Exact = true });
        await Assertions.Expect(bind).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 20_000 });
        await bind.ClickAsync();
        var deployment = await WaitForProjectionAsync(secret, 1, "Ready", token);
        Assert.True(await fixture.KubernetesSecretExistsAsync(group, $"pole-secret-{secret}-{deployment}", token));
        await CaptureAsync(page, "bindings");
        await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Projections", Exact = true }).ClickAsync();
        await Assertions.Expect(content.GetByText("Projected to target", new LocatorGetByTextOptions { Exact = true })).ToBeVisibleAsync();
        await CaptureAsync(page, "projections");
        await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Rotate values", Exact = true }).ClickAsync();
        await CaptureAsync(page, "rotate");
        await content.GetByLabel("Value 1").FillAsync($"test-{Guid.NewGuid():N}");
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Review replacement", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Rotate Secret", Exact = true }).ClickAsync();
        await WaitForProjectionAsync(secret, 2, "Ready", token);
        await page.ReloadAsync();
        await Assertions.Expect(content.GetByLabel("Value 1")).ToHaveValueAsync("");
        await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Bindings", Exact = true }).ClickAsync();
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Detach binding", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Detach binding", Exact = true }).ClickAsync();
        await WaitForProjectionAsync(secret, 2, "Deleted", token);
        Assert.False(await fixture.KubernetesSecretExistsAsync(group, $"pole-secret-{secret}-{deployment}", token));
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete Secret", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete Secret", Exact = true }).ClickAsync();
        await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Secrets", Exact = true }).WaitForAsync();
    }

    private static object Probe() => new { path = "/", port = "80", initialDelaySeconds = 0, periodSeconds = 1, timeoutSeconds = 1, successThreshold = 1, failureThreshold = 30 };

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = fixture.CreateAuthenticatedRequest(method, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<JsonElement>(token);
    }

    private async Task<string> WaitForProjectionAsync(string id, int version, string health, CancellationToken token)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromMinutes(3));
        while (true)
        {
            var projections = await SendAsync(HttpMethod.Get, $"/api/v1/secrets/{id}/projections", null, bounded.Token);
            foreach (var row in projections.EnumerateArray())
                if (row.GetProperty("health").GetString() == health && row.GetProperty("desiredVersion").GetInt64() == version && (health == "Deleted" || row.GetProperty("appliedVersion").GetInt64() == version)) return row.GetProperty("resourceDeploymentId").GetString()!;
            await Task.Delay(1000, bounded.Token);
        }
    }

    private async Task CleanupSecretAsync(string id, CancellationToken token)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/secrets/{id}/usage", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        response.EnsureSuccessStatusCode();
        var usage = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        foreach (var row in usage.EnumerateArray()) await SendAsync(HttpMethod.Delete, $"/api/v1/secrets/{id}/bindings/{row.GetProperty("binding").GetProperty("id").GetString()}", null, token);
        await DeleteResourceAsync("secrets", id, token);
    }

    private async Task DeleteResourceAsync(string route, string id, CancellationToken token)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromMinutes(1));
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/v1/{route}/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, bounded.Token);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);
        while (true)
        {
            using var read = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/{route}/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var remaining = await fixture.Api.SendAsync(read, bounded.Token);
            if (remaining.StatusCode == HttpStatusCode.NotFound) return;
            remaining.EnsureSuccessStatusCode();
            await Task.Delay(1000, bounded.Token);
        }
    }

    private async Task DeleteGroupAsync(string group, CancellationToken token)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/v1/resource-groups/{group}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, token);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);
        await fixture.WaitForNamespaceDeletionAsync(group, token);
    }

    private async Task CaptureAsync(IPage page, string section)
    {
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "frontend-secrets"));
        Directory.CreateDirectory(directory);
        foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var theme in new[] { "light", "dark" })
            {
                if (await page.Locator("html").GetAttributeAsync("data-theme") != theme) await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Switch to {theme} theme", Exact = true }).ClickAsync();
                await page.EvaluateAsync("window.scrollTo(0, 0)");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, $"secrets-{viewport}-{section}-{theme}.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), $"Secret workspace overflow: {viewport}/{section}/{theme}.");
            }
        }
        await page.SetViewportSizeAsync(1440, 900);
        output.WriteLine($"Secret screenshots captured: {section}");
    }
}
