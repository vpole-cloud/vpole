using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class DomainBrowserE2ETests(PoleE2EFixture fixture)
{
    [Fact]
    public async Task Tenant_can_reserve_inspect_replace_and_detach_exact_target_hostnames_in_separate_workspaces()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        await using var resources = new TestResourceScope("domain browser E2E");
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var groupName = $"domains-{suffix}";
        var group = await SendAsync(HttpMethod.Post,
            "/api/v1/resource-groups",
            new
            {
                name = groupName,
                description = "Domain browser test",
                tags = new[] { "e2e" }
            },
            ct);
        var groupId = Text(group, "id");
        resources.Defer("delete domain browser group", cleanup => DeleteGroupAsync(groupId, cleanup));
        await using var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
        await browserContainer.StartAsync(ct);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint,
            new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            IgnoreHTTPSErrors = true
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
        await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
        await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true })
            .FillAsync(fixture.Alpha.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
        await page.GetByLabel("Tenant").SelectOptionAsync(fixture.Alpha.DefaultTenantId);
        await page.GotoAsync(Url("domains", "catalog"));
        var content = page.Locator("#app-content");
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Reserve zone", Exact = true })
            .ClickAsync();
        var target = content.GetByLabel("Deployment target", new LocatorGetByLabelOptions { Exact = true });
        await Assertions.Expect(target.Locator("option")).ToHaveCountAsync(2);
        await target.SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await content.GetByLabel("Zone label", new LocatorGetByLabelOptions { Exact = true }).FillAsync($"ui-{suffix}");
        await CaptureAsync(page, "reserve");
        var reservation = await page.RunAndWaitForResponseAsync(() =>
                content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Reserve zone", Exact = true })
                    .ClickAsync(),
            response => response.Request.Method == "POST" &&
                        new Uri(response.Url).AbsolutePath.TrimEnd('/') == "/api/v1/domain-zones");
        Assert.Equal(201, reservation.Status);
        var zone = (await reservation.JsonAsync())!.Value;
        var zoneId = Text(zone, "id");
        var domain = Text(zone, "domain");
        resources.Defer("delete browser domain reservation",
            cleanup => DeleteAsync($"/api/v1/domain-zones/{zoneId}", cleanup));
        await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = domain, Exact = true })
            .WaitForAsync();
        var appName = $"web-{suffix}";
        var probe = new
        {
            path = "/",
            port = "80",
            initialDelaySeconds = 0,
            periodSeconds = 2,
            timeoutSeconds = 1,
            failureThreshold = 30,
            successThreshold = 1
        };
        var app = await SendAsync(HttpMethod.Post,
            "/api/v1/web-services",
            new
            {
                name = appName,
                resourceGroupId = groupId,
                exposure = "Public",
                tags = new[] { "e2e" },
                spec = new
                {
                    image = new
                    {
                        server = "docker.io",
                        name = "library/nginx",
                        tag = "1.27.5-alpine"
                    },
                    config = new
                    {
                        environmentVariables = new Dictionary<string, string>(),
                        portMappings = new[]
                            { new { servicePort = 80, targetPort = "80", protocol = "TCP", name = "http" } }
                    },
                    autoscaling = (object?)null,
                    probes = new
                    {
                        startup = probe,
                        liveness = probe,
                        readiness = probe
                    }
                },
                placement = new
                {
                    targets = new[] { new { deploymentTargetId = Text(zone, "deploymentTargetId"), fixedReplicas = 1 } }
                }
            },
            ct);
        var appId = Text(app, "webServiceId");
        resources.Defer("delete domain browser application", cleanup => DeleteWebServiceAsync(appId, cleanup));
        await fixture.WaitForNamespaceCreationAsync(groupId, ct);
        await fixture.WaitForWebServiceReadyAsync(groupId, appName, ct);
        await page.GotoAsync(Url("web-services", appId, "domains"));
        await content.GetByRole(AriaRole.Heading,
            new LocatorGetByRoleOptions { Name = "Domains and hostnames", Exact = true }).WaitForAsync();
        await content.GetByLabel("Reserved zone", new LocatorGetByLabelOptions { Exact = true })
            .SelectOptionAsync(zoneId);
        await content.GetByLabel("Hostname label", new LocatorGetByLabelOptions { Exact = true }).FillAsync("api");
        var attachRequestsStarted = 0;
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" &&
                new Uri(request.Url).AbsolutePath.EndsWith("/hostnames", StringComparison.Ordinal))
                Interlocked.Increment(ref attachRequestsStarted);
        };
        IResponse attached;
        try
        {
            attached = await page.RunAndWaitForResponseAsync(() =>
                    content.GetByRole(AriaRole.Button,
                            new LocatorGetByRoleOptions { Name = "Attach alias", Exact = true })
                        .ClickAsync(),
                response => response.Request.Method == "POST" &&
                            new Uri(response.Url).AbsolutePath.EndsWith("/hostnames", StringComparison.Ordinal));
        }
        catch (Exception failure) when (failure is TimeoutException or PlaywrightException)
        {
            // Record only a count: headers and mutation payloads may contain private data.
            throw new InvalidOperationException(
                $"The domain alias submit did not receive its awaited response. Matching requests started: {Volatile.Read(ref attachRequestsStarted)}.",
                failure);
        }

        Assert.Equal(202, attached.Status);
        await page.ReloadAsync();
        await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "api." + domain, Exact = true })
            .WaitForAsync();
        await CaptureAsync(page, "service");
        await page.GotoAsync(Url("domains", zoneId));
        await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = appName, Exact = true })
            .WaitForAsync();
        await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = groupName, Exact = true })
            .WaitForAsync();
        await Assertions
            .Expect(content.GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Remove reservation", Exact = true })).ToBeDisabledAsync();
        await CaptureAsync(page, "ownership");
        await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = appName, Exact = true })
            .ClickAsync();
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Change alias", Exact = true })
            .ClickAsync();
        await content.GetByLabel("Hostname label", new LocatorGetByLabelOptions { Exact = true }).FillAsync("next");
        var detached = await page.RunAndWaitForResponseAsync(() =>
                content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Replace alias", Exact = true })
                    .ClickAsync(),
            response => response.Request.Method == "DELETE" &&
                        new Uri(response.Url).AbsolutePath.Contains("/hostnames/", StringComparison.Ordinal));
        Assert.Equal(202, detached.Status);
        await page.ReloadAsync();
        await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "next." + domain, Exact = true })
            .WaitForAsync();
        Assert.Equal(0,
            await content
                .GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "api." + domain, Exact = true })
                .CountAsync());
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Detach alias", Exact = true })
            .ClickAsync();
        await page.GetByRole(AriaRole.Dialog)
            .GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Detach alias", Exact = true })
            .ClickAsync();
        await Assertions
            .Expect(content.GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Detach alias", Exact = true })).ToHaveCountAsync(0);
        Assert.Equal(appId, Text(await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{appId}", null, ct), "id"));
        await page.GotoAsync(Url("domains", zoneId));
        await Assertions
            .Expect(content.GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Remove reservation", Exact = true })).ToBeEnabledAsync();
    }

    private string Url(string view, string resource, string section = "overview") => new Uri(
        fixture.FrontendBaseAddress,
        $"?view={view}&resource={resource}&section={section}&tenant={fixture.Alpha.DefaultTenantId}").ToString();

    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request =
            fixture.CreateAuthenticatedRequest(method, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, $"Domain API {method} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var request =
            fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound,
            $"Domain cleanup returned HTTP {(int)response.StatusCode}.");
    }

    private async Task DeleteWebServiceAsync(string id, CancellationToken ct)
    {
        await DeleteAsync($"/api/v1/web-services/{id}", ct);
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get,
                $"/api/v1/web-services/{id}",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new TimeoutException("Domain browser application did not finish deletion.");
    }

    private async Task DeleteGroupAsync(string id, CancellationToken ct)
    {
        await DeleteAsync($"/api/v1/resource-groups/{id}", ct);
        await fixture.WaitForNamespaceDeletionAsync(id, ct);
    }

    private static async Task CaptureAsync(IPage page, string section)
    {
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "frontend-domains"));
        Directory.CreateDirectory(directory);
        foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var theme in new[] { "light", "dark" })
            {
                if (await page.Locator("html").GetAttributeAsync("data-theme") != theme)
                    await page.GetByRole(AriaRole.Button,
                        new PageGetByRoleOptions { Name = $"Switch to {theme} theme", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
                var selectedSection =
                    page.Locator("#app-content [data-testid='section-tabs'] [role=tab][aria-selected=true]:visible");
                if (await selectedSection.CountAsync() > 0)
                    Assert.True(await selectedSection.EvaluateAsync<bool>(
                            "tab => { const bounds = tab.getBoundingClientRect(); const list = tab.closest('[role=tablist]').getBoundingClientRect(); return bounds.left >= list.left - 1 && bounds.right <= list.right + 1; }"),
                        "The active resource section must remain visible after viewport resizing.");
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(directory, $"domains-{viewport}-{section}-{theme}.png"),
                    FullPage = true,
                    Animations = ScreenshotAnimations.Disabled
                });
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"),
                    "The domain workspace overflows its viewport.");
            }
        }

        await page.SetViewportSizeAsync(1440, 900);
    }
}
