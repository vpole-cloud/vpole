using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class HealthBrowserE2ETests(
    PoleE2EFixture fixture
)
{
    private readonly TestTimings _timings = new(typeof(HealthBrowserE2ETests));

    [Fact]
    public async Task Applied_unready_workload_is_observed_independently_and_operations_restore_after_reload()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var setupTiming = _timings.Measure("e2e.setup");
        using var testEvidence = new TestPhaseJournal(
            nameof(Applied_unready_workload_is_observed_independently_and_operations_restore_after_reload)).Activate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var resources = new TestResourceScope("health browser E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            var suffix = Guid.NewGuid().ToString("N")[..10];
            var name = $"health-{suffix}";
            var group = await SendAsync(
                HttpMethod.Post,
                "/api/v1/resource-groups",
                new { name, description = "Health browser test", tags = new[] { "e2e" } },
                ct);
            var groupId = Text(group, "id");
            resources.Defer("delete health browser group", cleanup => DeleteGroupAsync(groupId, cleanup));
            var targets = await SendAsync(
                HttpMethod.Get,
                "/api/v1/deployment-targets/available?resourceType=pole%2Fweb-service&requiresPublicIngress=false",
                null,
                ct);
            var targetId = Text(targets.EnumerateArray().First(), "id");
            var app = await SendAsync(
                HttpMethod.Post,
                "/api/v1/web-services",
                new
                {
                    name,
                    resourceGroupId = groupId,
                    exposure = "Private",
                    tags = new[] { "e2e" },
                    spec = Spec(false),
                    placement = new { targets = new[] { new { deploymentTargetId = targetId, fixedReplicas = 1 } } },
                },
                ct);
            var id = Text(app, "webServiceId");
            resources.Defer("delete health browser application", cleanup => DeleteWebServiceAsync(id, cleanup));
            await fixture.WaitForNamespaceCreationAsync(groupId, ct);
            await WaitForObservationAsync(id, false, ct);
            string browserEndpoint;
            using (_timings.Measure("e2e.browser.acquire"))
            {
                browserEndpoint = await fixture.GetBrowserEndpointAsync(ct);
            }

            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(
                browserEndpoint,
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            setupTiming.Dispose();
            using var scenarioTiming = _timings.Measure("e2e.scenario");
            var context = await browser.NewContextAsync(
                new BrowserNewContextOptions
                    { ViewportSize = new ViewportSize { Width = 1440, Height = 900 }, IgnoreHTTPSErrors = true });
            await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
            var page = await BrowserEvidence.NewPageAsync(context);
            await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true })
                .ClickAsync();
            await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
            await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true })
                .FillAsync(fixture.Alpha.Password);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true })
                .ClickAsync();
            await page.GetByLabel("Tenant").SelectOptionAsync(fixture.Alpha.DefaultTenantId);
            await page.GotoAsync(Url("web-services", id, "operations"));
            var content = page.Locator("#app-content");
            await Assertions.Expect(content.GetByTestId("readiness-status"))
                .ToHaveTextAsync("Not ready", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
            await Assertions.Expect(content.Locator("[data-testid='operation-status']:visible"))
                .ToHaveTextAsync("Applied");
            var healthTarget = content.Locator("[data-testid^='health-target-']:visible");
            var warnings = healthTarget.Locator("details");
            var warningsToggle = healthTarget.GetByText("Pods and warnings");
            await warningsToggle.ClickAsync();
            await Assertions.Expect(warnings).ToHaveAttributeAsync("open", "");
            await warningsToggle.PressAsync("Enter");
            await Assertions.Expect(warnings).ToHaveJSPropertyAsync("open", false);
            await warningsToggle.PressAsync("Space");
            await Assertions.Expect(warnings).ToHaveAttributeAsync("open", "");
            await Assertions
                .Expect(warnings.GetByText("Unhealthy", new LocatorGetByTextOptions { Exact = false }).First)
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
            await CaptureAsync(page, "not-ready", id, "Operations and health");
            var current = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{id}", null, ct);
            var accepted = await SendAsync(
                HttpMethod.Put,
                $"/api/v1/web-services/{id}",
                new
                {
                    spec = Spec(true), tags = new[] { "e2e" }, exposure = "Private",
                    expectedRevision = current.GetProperty("activeRevision").GetInt64(),
                },
                ct);
            var generation = accepted.GetProperty("deployments")[0].GetProperty("desiredGeneration").GetInt64();
            Assert.True(generation > 1);
            await page.ReloadAsync();
            await Assertions.Expect(
                    content.GetByRole(
                        AriaRole.Heading,
                        new LocatorGetByRoleOptions { Name = "Operations and health", Exact = true }))
                .ToBeVisibleAsync();
            await Assertions.Expect(
                    content.GetByText(
                        $"desired generation {generation}",
                        new LocatorGetByTextOptions { Exact = false }))
                .ToBeVisibleAsync();
            await WaitForObservationAsync(id, true, ct, generation);
            await content.GetByRole(
                    AriaRole.Button,
                    new LocatorGetByRoleOptions { Name = "Refresh operations", Exact = true })
                .ClickAsync();
            await Assertions.Expect(content.GetByTestId("readiness-status"))
                .ToHaveTextAsync("Ready", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
            await CaptureAsync(page, "ready", id, "Operations and health");
            const string operationsRoute = "**/api/v1/resource-deployments/operations?*";
            await page.RouteAsync(operationsRoute, route => route.AbortAsync("failed"));
            await content.GetByRole(
                    AriaRole.Button,
                    new LocatorGetByRoleOptions { Name = "Refresh operations", Exact = true })
                .ClickAsync();
            await Assertions.Expect(
                    content.GetByText(
                        "Previous operation state is stale.",
                        new LocatorGetByTextOptions { Exact = false }))
                .ToBeVisibleAsync();
            await Assertions.Expect(content.Locator("[data-testid^='operation-row-']:visible")).ToHaveCountAsync(1);
            Assert.Equal(
                0,
                await content.GetByRole(
                        AriaRole.Button,
                        new LocatorGetByRoleOptions { Name = "Retry this apply", Exact = true })
                    .CountAsync());
            await CaptureAsync(page, "stale-read", id, "Operations and health");
            await page.UnrouteAsync(operationsRoute);
            await content.GetByRole(
                    AriaRole.Button,
                    new LocatorGetByRoleOptions { Name = "Refresh operations", Exact = true })
                .ClickAsync();
            await Assertions.Expect(
                    content.GetByText(
                        "Previous operation state is stale.",
                        new LocatorGetByTextOptions { Exact = false }))
                .ToHaveCountAsync(0);
            await Assertions.Expect(content.GetByTestId("readiness-status"))
                .ToHaveTextAsync("Ready", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
            await page.GotoAsync(Url("resource-groups", groupId, "operations"));
            await Assertions.Expect(page.Locator("#app-content").GetByTestId("readiness-status"))
                .ToHaveTextAsync("Ready", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
            await CaptureAsync(page, "group", groupId, "Operations and health");
            await page.GotoAsync(Url("overview", "catalog"));
            await Assertions.Expect(
                    page.Locator("#app-content")
                        .GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = name, Exact = true })
                        .First)
                .ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#app-content").GetByTestId("readiness-status"))
                .ToHaveTextAsync("Ready", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
            await CaptureAsync(page, "tenant", "catalog", "Control plane");
        });
    }

    private static object Spec(bool ready)
    {
        var probe = new
        {
            path = "/",
            port = "80",
            initialDelaySeconds = 0,
            periodSeconds = 6,
            timeoutSeconds = 1,
            failureThreshold = 10,
            successThreshold = 1,
        };
        return new
        {
            image = new { server = "docker.io", name = "library/nginx", tag = "1.27.5-alpine" },
            config = new
            {
                environmentVariables = new Dictionary<string, string>(),
                portMappings = new[] { new { servicePort = 80, targetPort = "80", protocol = "TCP", name = "http" } },
            },
            autoscaling = (object?)null,
            probes = new
            {
                startup = probe, liveness = probe,
                readiness = new
                {
                    path = ready ? "/" : "/not-ready", port = "80", initialDelaySeconds = 0, periodSeconds = 2,
                    timeoutSeconds = 1, failureThreshold = 1, successThreshold = 1,
                },
            },
        };
    }

    private Task WaitForObservationAsync(
        string id,
        bool ready,
        CancellationToken ct,
        long minimumGeneration = 1) => TestWait.UntilAsync(
        async probeToken =>
        {
            var resource = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{id}", null, probeToken);
            var placement = resource.GetProperty("deployments")[0];
            var desired = placement.GetProperty("desiredGeneration").GetInt64();
            if (desired < minimumGeneration || Text(placement, "status") != "Applied" ||
                placement.GetProperty("observedGeneration").GetInt64() != desired)
                return false;
            var observed = await SendAsync(
                HttpMethod.Get,
                $"/api/v1/resource-deployments/{Text(placement, "deploymentId")}/observation",
                null,
                probeToken);
            if (observed.GetProperty("desiredGeneration").GetInt64() != desired ||
                observed.GetProperty("deploymentRevision").GetInt64() !=
                placement.GetProperty("deploymentRevision").GetInt64())
                return false;
            var value = observed.GetProperty("observation");
            return Text(value, "kind") == "Observed" && value.GetProperty("ready").GetBoolean() == ready &&
                value.GetProperty("rolloutCurrent").GetBoolean();
        },
        TimeSpan.FromMinutes(4),
        $"Expected owned native readiness={ready} was not observed for the accepted rollout.",
        ct,
        pollInterval: TimeSpan.FromSeconds(2));

    private string Url(
        string view,
        string resource,
        string section = "overview") => new Uri(
        fixture.FrontendBaseAddress,
        $"?view={view}&resource={resource}&section={section}&tenant={fixture.Alpha.DefaultTenantId}").ToString();

    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(
            method,
            path,
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, $"Health API {method} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteAsync");
        using var request = fixture.CreateAuthenticatedRequest(
            HttpMethod.Delete,
            path,
            fixture.Alpha,
            fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound,
            $"Health cleanup returned HTTP {(int)response.StatusCode}.");
    }

    private async Task DeleteWebServiceAsync(string id, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteWebServiceAsync");
        await DeleteAsync($"/api/v1/web-services/{id}", ct);
        await TestWait.UntilAsync(
            async probeToken =>
            {
                using var request = fixture.CreateAuthenticatedRequest(
                    HttpMethod.Get,
                    $"/api/v1/web-services/{id}",
                    fixture.Alpha,
                    fixture.Alpha.DefaultTenantId);
                using var response = await fixture.Api.SendAsync(request, probeToken);
                return TestDeletion.IsComplete(response.StatusCode);
            },
            TimeSpan.FromMinutes(3),
            "Health browser application did not finish deletion.",
            ct,
            pollInterval: TimeSpan.FromSeconds(2));
    }

    private async Task DeleteGroupAsync(string id, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteGroupAsync");
        await DeleteAsync($"/api/v1/resource-groups/{id}", ct);
        await fixture.WaitForNamespaceDeletionAsync(id, ct);
    }

    private static async Task CaptureAsync(
        IPage page,
        string section,
        string resourceId,
        string heading)
    {
        await BrowserWorkspaceReadiness.AssertAsync(page, resourceId, heading);
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "frontend-health"));
        Directory.CreateDirectory(directory);
        foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var theme in new[] { "light", "dark" })
            {
                if (await page.Locator("html").GetAttributeAsync("data-theme") != theme)
                    await page.GetByRole(
                            AriaRole.Button,
                            new PageGetByRoleOptions { Name = $"Switch to {theme} theme", Exact = true })
                        .ClickAsync();
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
                var selectedSection = page.Locator(
                    "#app-content [data-testid='section-tabs'] [role=tab][aria-selected=true]:visible");
                if (await selectedSection.CountAsync() > 0)
                    Assert.True(
                        await selectedSection.EvaluateAsync<bool>(
                            "tab => { const bounds = tab.getBoundingClientRect(); const list = tab.closest('[role=tablist]').getBoundingClientRect(); return bounds.left >= list.left - 1 && bounds.right <= list.right + 1; }"),
                        "The active resource section must remain visible after viewport resizing.");
                await page.ScreenshotAsync(
                    new PageScreenshotOptions
                    {
                        Path = Path.Combine(directory, $"health-{viewport}-{section}-{theme}.png"), FullPage = true,
                        Animations = ScreenshotAnimations.Disabled,
                    });
                Assert.True(
                    await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"),
                    "The health workspace overflows its viewport.");
            }
        }

        await page.SetViewportSizeAsync(1440, 900);
    }
}
