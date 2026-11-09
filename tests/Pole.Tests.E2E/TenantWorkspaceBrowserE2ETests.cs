using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Features.Core.Multitenancy;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class TenantWorkspaceBrowserE2ETests(PoleE2EFixture fixture)
{
    private readonly TestTimings _timings = new(typeof(TenantWorkspaceBrowserE2ETests));

    [Fact]
    public async Task Viewer_reads_real_workloads_and_tenant_switch_discards_only_confirmed_drafts_and_old_reads()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(nameof(Viewer_reads_real_workloads_and_tenant_switch_discards_only_confirmed_drafts_and_old_reads)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = budget.Token;
        var resources = new TestResourceScope("tenant workspace browser E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            var alphaTenant = fixture.Alpha.DefaultTenantId;
            var betaTenant = fixture.Beta.DefaultTenantId;
            var member = await SendAsync(HttpMethod.Post, $"/api/v1/tenants/{alphaTenant}/members", fixture.Alpha, alphaTenant,
                new
                {
                    userId = fixture.Beta.KeycloakUserId,
                    email = fixture.Beta.Email,
                    role = TenantRole.Viewer.ToString()
                }, ct);
            var memberId = Text(member, "id");
            resources.Defer("remove temporary Alpha Viewer", token => DeleteAsync($"/api/v1/tenants/{alphaTenant}/members/{memberId}", fixture.Alpha, alphaTenant, token));
            var suffix = Guid.NewGuid().ToString("N")[..10];
            var alphaName = $"a-view-{suffix}";
            var betaName = $"b-owner-{suffix}";
            var alphaGroup = await CreateGroupAsync(fixture.Alpha, alphaTenant, alphaName, ct);
            resources.Defer("delete Alpha viewer group", token => DeleteGroupAsync(alphaGroup, fixture.Alpha, alphaTenant, token));
            var betaGroup = await CreateGroupAsync(fixture.Beta, betaTenant, betaName, ct);
            resources.Defer("delete Beta owner group", token => DeleteGroupAsync(betaGroup, fixture.Beta, betaTenant, token));
            var targets = await SendAsync(HttpMethod.Get, "/api/v1/deployment-targets/available?resourceType=pole%2Fweb-service&requiresPublicIngress=false", fixture.Alpha, alphaTenant, null, ct);
            var probe = new
            {
                path = "/",
                port = "80",
                initialDelaySeconds = 0,
                periodSeconds = 3,
                timeoutSeconds = 1,
                successThreshold = 1,
                failureThreshold = 10
            };
            var created = await SendAsync(HttpMethod.Post, "/api/v1/web-services", fixture.Alpha, alphaTenant, new
            {
                name = alphaName,
                resourceGroupId = alphaGroup,
                exposure = "Private",
                tags = new[] { "e2e" },
                spec = new
                {
                    image = new { server = "docker.io", name = "library/nginx", tag = "1.27.5-alpine" },
                    config = new { environmentVariables = new Dictionary<string, string>(), portMappings = new[] { new { name = "http", servicePort = 80, targetPort = "80", protocol = "TCP" } } },
                    autoscaling = (object?)null,
                    probes = new { startup = probe, liveness = probe, readiness = probe }
                },
                placement = new { targets = new[] { new { deploymentTargetId = Text(targets.EnumerateArray().First(), "id"), fixedReplicas = 1 } } }
            }, ct);
            var serviceId = Text(created, "webServiceId");
            resources.Defer("delete Alpha viewer service", token => DeleteServiceAsync(serviceId, alphaTenant, token));
            await fixture.WaitForNamespaceCreationAsync(alphaGroup, ct);
            await fixture.WaitForWebServiceReadyAsync(alphaGroup, alphaName, ct);
            using (var denied = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/v1/web-services/{serviceId}", fixture.Beta, alphaTenant))
            using (var response = await fixture.Api.SendAsync(denied, ct))
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            await using var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
            await browserContainer.StartAsync(ct);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint, new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 }, IgnoreHTTPSErrors = true });
            var page = await BrowserEvidence.NewPageAsync(context);
            await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
            await page.GetByLabel("Username or email").FillAsync(fixture.Beta.Email);
            await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true }).FillAsync(fixture.Beta.Password);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
            await page.GetByLabel("Tenant").SelectOptionAsync(betaTenant);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Web services", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "New service", Exact = true }).ClickAsync();
            await page.GetByLabel("Name", new PageGetByLabelOptions { Exact = true }).FillAsync("unsaved-owner-draft");
            await page.GetByLabel("Tenant").SelectOptionAsync(alphaTenant);
            await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Cancel", Exact = true }).ClickAsync();
            Assert.Equal(betaTenant, await page.GetByLabel("Tenant").InputValueAsync());
            Assert.Equal("unsaved-owner-draft", await page.GetByLabel("Name", new PageGetByLabelOptions { Exact = true }).InputValueAsync());
            await page.GetByLabel("Tenant").SelectOptionAsync(alphaTenant);
            await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Discard and continue", Exact = true }).ClickAsync();
            var content = page.Locator("#app-content");
            await Assertions.Expect(content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = alphaName, Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "New service", Exact = true })).ToBeDisabledAsync();
            await Assertions.Expect(content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = $"Delete {alphaName}", Exact = true })).ToBeDisabledAsync();
            await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = alphaName, Exact = true }).ClickAsync();
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Operations", Exact = true }).ClickAsync();
            await Assertions.Expect(content.GetByTestId("readiness-status")).ToHaveTextAsync("Ready", new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
            Assert.Equal(0, await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete service", Exact = true }).CountAsync());
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Runtime logs and events", Exact = true }).ClickAsync();
            var service = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{serviceId}", fixture.Beta, alphaTenant, null, ct);
            var deploymentId = Text(service.GetProperty("deployments").EnumerateArray().Single(), "deploymentId");
            JsonElement ownedInstance = default;
            await TestWait.UntilAsync(async phase =>
            {
                var snapshot = await SendAsync(HttpMethod.Get, $"/api/v1/resource-deployments/{deploymentId}/diagnostics", fixture.Beta, alphaTenant, null, phase);
                var diagnostics = snapshot.GetProperty("diagnostics");
                if (Text(diagnostics, "kind") != "Available") return false;
                ownedInstance = diagnostics.GetProperty("instances").EnumerateArray()
                    .Where(item => item.GetProperty("ready").GetBoolean() && item.GetProperty("containers").EnumerateArray().Any(container => Text(container, "name") == alphaName))
                    .OrderBy(item => Text(item, "uid"), StringComparer.Ordinal).FirstOrDefault();
                return ownedInstance.ValueKind == JsonValueKind.Object;
            }, TimeSpan.FromSeconds(30), "The owned viewer runtime instance was not ready.", ct);
            var uid = Text(ownedInstance, "uid");
            var instance = page.GetByLabel("Instance", new PageGetByLabelOptions { Exact = true });
            await Assertions.Expect(instance.Locator($"option[value='{uid}']")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
            await instance.SelectOptionAsync(uid);
            await page.GetByLabel("Container", new PageGetByLabelOptions { Exact = true }).SelectOptionAsync(alphaName);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observation = new TestActionObservation<IRequest>();
            IRequest? followed = null;
            var switchingTenant = false;
            void OnRequest(object? sender, IRequest request)
            {
                if (BrowserPhaseJournal.IsRuntimeRequest(request, deploymentId, uid, alphaName, true))
                {
                    followed = request;
                    observation.TryObserve(request);
                }
            }
            page.Request += OnRequest;
            page.RequestFailed += (_, request) => { if (switchingTenant && ReferenceEquals(request, followed)) canceled.TrySetResult(); };
            try
            {
                await observation.RunAsync(() => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Follow runtime logs", Exact = true }).ClickAsync(), TimeSpan.FromSeconds(5), ct);
            }
            finally { page.Request -= OnRequest; }
            await Assertions.Expect(page.GetByLabel("Runtime log output", new PageGetByLabelOptions { Exact = true })).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
            await CaptureAsync(page, "viewer");

            const string operationsRoute = "**/api/v1/resource-deployments/operations?*";
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var intercept = 0;
            await page.RouteAsync(operationsRoute, async route =>
            {
                if (!route.Request.Headers.TryGetValue("x-pole-tenant-id", out var tenant) || tenant != alphaTenant || Interlocked.Exchange(ref intercept, 1) != 0)
                {
                    await route.ContinueAsync();
                    return;
                }
                try
                {
                    var realResponse = await route.FetchAsync();
                    try
                    {
                        Assert.True(realResponse.Ok, "The held Alpha operation response must come from the real API.");
                        held.TrySetResult();
                        await release.Task.WaitAsync(ct);
                        try { await route.FulfillAsync(new RouteFulfillOptions { Response = realResponse }); }
                        catch (PlaywrightException) when (switchingTenant) { /* Expected canceled old-scope delivery. */ }
                        delivered.TrySetResult();
                    }
                    finally { await realResponse.DisposeAsync(); }
                }
                catch
                {
                    var failure = new InvalidOperationException("The held Alpha operation phase failed.");
                    held.TrySetException(failure);
                    delivered.TrySetException(failure);
                }
            });
            try
            {
                await page.EvaluateAsync("window.dispatchEvent(new Event('online'))");
                await held.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                switchingTenant = true;
                await page.GetByLabel("Tenant").SelectOptionAsync(betaTenant);
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                release.TrySetResult();
                await delivered.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                await Assertions.Expect(content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "New service", Exact = true })).ToBeVisibleAsync();
                Assert.Equal(0, await page.GetByLabel("Runtime log output", new PageGetByLabelOptions { Exact = true }).CountAsync());
                Assert.True(!(await content.InnerTextAsync()).Contains(alphaName, StringComparison.Ordinal), "The new tenant workspace must exclude late Alpha rows and runtime output.");
                var storage = await page.EvaluateAsync<string>("JSON.stringify(Object.fromEntries(Object.entries(localStorage).filter(([key]) => key.endsWith('.' + " + JsonSerializer.Serialize(betaTenant) + "))))");
                Assert.True(storage != "{}", "The check must inspect an existing Beta workspace record.");
                Assert.True(!storage.Contains(serviceId, StringComparison.Ordinal), "Beta workspace persistence must exclude Alpha resource identities.");
                await CaptureAsync(page, "owner-after-switch");
            }
            finally { release.TrySetResult(); await page.UnrouteAsync(operationsRoute); }
        });
    }

    private async Task<string> CreateGroupAsync(TestUser user, string tenant, string name, CancellationToken ct) =>
        Text(await SendAsync(HttpMethod.Post, "/api/v1/resource-groups", user, tenant, new { name, description = "Tenant browser acceptance", tags = new[] { "e2e" } }, ct), "id");

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, TestUser user, string tenant, object? body, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(method, path, user, tenant);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, $"Tenant browser API {method} {path} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
    private async Task DeleteAsync(string path, TestUser user, string tenant, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteAsync");
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, user, tenant);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound, $"Tenant browser cleanup returned HTTP {(int)response.StatusCode}.");
    }
    private async Task DeleteGroupAsync(string id, TestUser user, string tenant, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteGroupAsync");
        await DeleteAsync($"/api/v1/resource-groups/{id}", user, tenant, ct);
        await fixture.WaitForNamespaceDeletionAsync(id, ct);
    }
    private async Task DeleteServiceAsync(string id, string tenant, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteServiceAsync");
        await DeleteAsync($"/api/v1/web-services/{id}", fixture.Alpha, tenant, ct);
        while (true)
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/web-services/{id}", fixture.Alpha, tenant);
            using var response = await fixture.Api.SendAsync(request, ct);
            if (TestDeletion.IsComplete(response.StatusCode)) return;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }
    private static string Text(JsonElement value, string key) => value.GetProperty(key).GetString()!;
    private static async Task CaptureAsync(IPage page, string section)
    {
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "frontend-tenant-acceptance"));
        Directory.CreateDirectory(directory);
        foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var theme in new[] { "light", "dark" })
            {
                if (await page.Locator("html").GetAttributeAsync("data-theme") != theme)
                    await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Switch to {theme} theme", Exact = true }).ClickAsync();
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, $"tenant-{viewport}-{section}-{theme}.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), "Tenant workspace must fit its viewport.");
            }
        }
        await page.SetViewportSizeAsync(1440, 900);
    }
}
