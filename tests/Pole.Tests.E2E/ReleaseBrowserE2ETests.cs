using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class ReleaseBrowserE2ETests(PoleE2EFixture fixture)
{
    private readonly TestTimings _timings = new(typeof(ReleaseBrowserE2ETests));

    [Fact]
    public async Task Redeploy_pending_survives_reload_and_rollback_restores_spec_with_a_new_ready_release()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var setupTiming = _timings.Measure("e2e.setup");
        using var testEvidence = new TestPhaseJournal(nameof(Redeploy_pending_survives_reload_and_rollback_restores_spec_with_a_new_ready_release)).Activate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        var resources = new TestResourceScope("release browser E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            var name = $"release-{Guid.NewGuid():N}"[..18];
            var group = await SendAsync(HttpMethod.Post, "/api/v1/resource-groups", new { name, description = "Release browser test", tags = new[] { "e2e" } }, ct);
            var groupId = Text(group, "id");
            resources.Defer("delete release browser group", cleanup => DeleteGroupAsync(groupId, cleanup));
            var targets = await SendAsync(HttpMethod.Get, "/api/v1/deployment-targets/available?resourceType=pole%2Fweb-service&requiresPublicIngress=false", null, ct);
            var targetId = Text(targets.EnumerateArray().First(), "id");
            var created = await SendAsync(HttpMethod.Post, "/api/v1/web-services", new
            {
                name,
                resourceGroupId = groupId,
                exposure = "Private",
                tags = new[] { "e2e" },
                spec = Spec("initial"),
                placement = new { targets = new[] { new { deploymentTargetId = targetId, fixedReplicas = 1 } } }
            }, ct);
            var id = Text(created, "webServiceId");
            resources.Defer("delete release browser application", cleanup => DeleteWebServiceAsync(id, cleanup));
            var targetRestorationRequired = false;
            resources.Defer("restore isolated fixture target availability", async cleanup => { if (targetRestorationRequired) await SetTargetEnabledAsync(targetId, true, cleanup); });
            await fixture.WaitForNamespaceCreationAsync(groupId, ct);
            await WaitForReleaseAsync(id, 1, "Succeeded", ct);
            await SendAsync(HttpMethod.Put, $"/api/v1/web-services/{id}", new { spec = Spec("changed"), tags = new[] { "e2e" }, exposure = "Private", expectedRevision = 1 }, ct);
            await WaitForReleaseAsync(id, 2, "Succeeded", ct);
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
            await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
            await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true }).FillAsync(fixture.Alpha.Password);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
            await page.GetByLabel("Tenant").SelectOptionAsync(fixture.Alpha.DefaultTenantId);
            await page.GotoAsync(Url(id, "releases"));
            var content = page.Locator("#app-content");
            await Assertions.Expect(content.GetByText("Current desired revision 2.", new LocatorGetByTextOptions { Exact = false })).ToBeVisibleAsync();
            // This fixture's target is isolated in its disposable Mongo/API graph. Keep the real
            // release Pending during reload, then allow the existing server retry to finish it.
            targetRestorationRequired = true;
            await SetTargetEnabledAsync(targetId, false, ct);
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Redeploy current revision", Exact = true }).ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);
            await Assertions.Expect(dialog.GetByText("Redeploy reapplies current configuration.", new LocatorGetByTextOptions { Exact = false })).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Confirm redeploy", Exact = true }).ClickAsync();
            await WaitForPlacementFailureAsync(id, ct);
            await page.ReloadAsync();
            var redeploy = content.Locator("[aria-label='Release revision 3']");
            await Assertions.Expect(redeploy.GetByTestId("release-status")).ToHaveTextAsync("Pending", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
            await Assertions.Expect(redeploy.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Revision 3 · Redeploy", Exact = true })).ToBeVisibleAsync();
            await CaptureAsync(page, "pending");
            await SetTargetEnabledAsync(targetId, true, ct);
            targetRestorationRequired = false;
            await WaitForReleaseAsync(id, 3, "Succeeded", ct);
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Refresh releases", Exact = true }).ClickAsync();
            await Assertions.Expect(redeploy.GetByTestId("release-status")).ToHaveTextAsync("Succeeded", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Rollback to revision 1", Exact = true }).ClickAsync();
            dialog = page.GetByRole(AriaRole.Dialog);
            await Assertions.Expect(dialog.GetByText("Database contents and Secret values are not restored.", new LocatorGetByTextOptions { Exact = false })).ToBeVisibleAsync();
            var rollbackResponse = await page.RunAndWaitForResponseAsync(
                () => dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Confirm rollback", Exact = true }).ClickAsync(),
                response => response.Request.Method == "POST" && response.Url.EndsWith($"/web-services/{id}/rollback", StringComparison.Ordinal));
            Assert.Equal(202, rollbackResponse.Status);
            // A click dispatches the handler; navigation must wait for server acceptance rather
            // than cancel its in-flight request and then wait for a release that never existed.
            await Assertions.Expect(dialog).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 30_000 });
            await Assertions.Expect(content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Revision 4 · Rollback", Exact = true })).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
            await page.ReloadAsync();
            await WaitForReleaseAsync(id, 4, "Succeeded", ct);
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Refresh releases", Exact = true }).ClickAsync();
            var rollback = content.Locator("[aria-label='Release revision 4']");
            await Assertions.Expect(rollback.GetByTestId("release-status")).ToHaveTextAsync("Succeeded", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
            await CaptureAsync(page, "rollback");
            var restored = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{id}", null, ct);
            Assert.Equal(4, restored.GetProperty("activeRevision").GetInt64());
            Assert.Equal("initial", restored.GetProperty("spec").GetProperty("config").GetProperty("environmentVariables").GetProperty("APP_MODE").GetString());
            Assert.Equal("Private", Text(restored.GetProperty("exposure"), "mode"));
            var placement = restored.GetProperty("deployments")[0];
            Assert.Equal(targetId, Text(placement, "deploymentTargetId"));
            Assert.Equal(1, placement.GetProperty("fixedReplicas").GetInt32());
            await WaitForReadyAsync(id, ct);
            await page.GotoAsync(Url(id, "operations"));
            await Assertions.Expect(content.GetByTestId("readiness-status")).ToHaveTextAsync("Ready", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
            await CaptureAsync(page, "ready");
        });
    }

    private static object Spec(string mode)
    {
        var probe = new
        {
            path = "/",
            port = "80",
            initialDelaySeconds = 0,
            periodSeconds = 6,
            timeoutSeconds = 1,
            failureThreshold = 10,
            successThreshold = 1
        };
        return new
        {
            image = new { server = "docker.io", name = "library/nginx", tag = "1.27.5-alpine" },
            config = new { environmentVariables = new Dictionary<string, string> { ["APP_MODE"] = mode }, portMappings = new[] { new { servicePort = 80, targetPort = "80", protocol = "TCP", name = "http" } } },
            autoscaling = (object?)null,
            probes = new { startup = probe, liveness = probe, readiness = probe }
        };
    }

    private async Task SetTargetEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        using var get = fixture.CreatePlatformAdminRequest(HttpMethod.Get, $"/api/v1/platform/deployment-targets/{id}");
        using var result = await fixture.Api.SendAsync(get, ct);
        Assert.True(result.IsSuccessStatusCode, $"Release target read returned HTTP {(int)result.StatusCode}.");
        var target = await result.Content.ReadFromJsonAsync<JsonElement>(ct);
        using var put = fixture.CreatePlatformAdminRequest(HttpMethod.Put, $"/api/v1/platform/deployment-targets/{id}");
        put.Content = JsonContent.Create(new { displayName = Text(target, "displayName"), enabled, acceptsWorkloads = enabled, workloadConfiguration = target.GetProperty("workloadConfiguration"), revision = target.GetProperty("revision").GetInt64() });
        using var response = await fixture.Api.SendAsync(put, ct);
        Assert.True(response.IsSuccessStatusCode, $"Release target update returned HTTP {(int)response.StatusCode}.");
    }

    private async Task WaitForPlacementFailureAsync(string id, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.readiness.WaitForPlacementFailureAsync");
        await TestWait.UntilAsync(async phase =>
        {
            var service = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{id}", null, phase);
            var placement = service.GetProperty("deployments")[0];
            if (Text(placement, "status") == "Failed" && Text(placement, "failureCode") == "DeploymentTargetDisabled") return true;
            return false;
        }, TimeSpan.FromMinutes(2), "The selected release target did not report its deliberate disabled-target failure.", ct, TimeSpan.FromSeconds(1));
    }

    private async Task WaitForReleaseAsync(string id, long revision, string outcome, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.readiness.WaitForReleaseAsync");
        await TestWait.UntilAsync(async phase =>
        {
            var history = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{id}/releases", null, phase);
            var release = history.GetProperty("releases").GetProperty("items").EnumerateArray().FirstOrDefault(value => value.GetProperty("resourceRevision").GetInt64() == revision);
            if (release.ValueKind == JsonValueKind.Object && Text(release, "outcome") == outcome) return true;
            return false;
        }, TimeSpan.FromMinutes(4), $"Release revision {revision} did not reach {outcome}.", ct, TimeSpan.FromSeconds(2));
    }

    private async Task WaitForReadyAsync(string id, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.readiness.WaitForReadyAsync");
        await TestWait.UntilAsync(async phase =>
        {
            var service = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{id}", null, phase);
            var placement = service.GetProperty("deployments")[0];
            var observed = await SendAsync(HttpMethod.Get, $"/api/v1/resource-deployments/{Text(placement, "deploymentId")}/observation", null, phase);
            var value = observed.GetProperty("observation");
            if (Text(value, "kind") == "Observed" && value.GetProperty("ready").GetBoolean() && observed.GetProperty("desiredGeneration").GetInt64() == placement.GetProperty("desiredGeneration").GetInt64()) return true;
            return false;
        }, TimeSpan.FromMinutes(4), "The restored release did not reach current native readiness.", ct, TimeSpan.FromSeconds(2));
    }

    private string Url(string id, string section) => new Uri(fixture.FrontendBaseAddress, $"?view=web-services&resource={id}&section={section}&tenant={fixture.Alpha.DefaultTenantId}").ToString();
    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(method, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, $"Release API {method} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
    private async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteAsync");
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound, $"Release cleanup returned HTTP {(int)response.StatusCode}.");
    }
    private async Task DeleteWebServiceAsync(string id, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteWebServiceAsync");
        await DeleteAsync($"/api/v1/web-services/{id}", ct);
        await TestWait.UntilAsync(async phase =>
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/web-services/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, phase);
            if (TestDeletion.IsComplete(response.StatusCode)) return true;
            return false;
        }, TimeSpan.FromMinutes(3), "Release browser application did not finish deletion.", ct, TimeSpan.FromSeconds(2));
    }

    private async Task DeleteGroupAsync(string id, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteGroupAsync");
        await DeleteAsync($"/api/v1/resource-groups/{id}", ct);
        await fixture.WaitForNamespaceDeletionAsync(id, ct);
    }
    private static async Task CaptureAsync(IPage page, string section)
    {
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "frontend-releases")); Directory.CreateDirectory(directory);
        foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var theme in new[] { "light", "dark" })
            {
                if (await page.Locator("html").GetAttributeAsync("data-theme") != theme) await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Switch to {theme} theme", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
                var selectedSection = page.Locator("#app-content [data-testid='section-tabs'] [role=tab][aria-selected=true]:visible");
                if (await selectedSection.CountAsync() > 0)
                    Assert.True(await selectedSection.EvaluateAsync<bool>("tab => { const bounds = tab.getBoundingClientRect(); const list = tab.closest('[role=tablist]').getBoundingClientRect(); return bounds.left >= list.left - 1 && bounds.right <= list.right + 1; }"), "The active resource section must remain visible after viewport resizing.");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, $"releases-{viewport}-{section}-{theme}.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), "The release workspace overflows its viewport.");
            }
        }
        await page.SetViewportSizeAsync(1440, 900);
    }
}
