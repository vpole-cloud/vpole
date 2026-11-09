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
public sealed class PublicServiceBindingsJourney(PoleE2EFixture fixture, ITestOutputHelper output)
{
    private readonly TestTimings _timings = new(typeof(PublicServiceBindingsJourney));

    [Fact]
    public async Task Secret_projection_and_domain_bindings_share_one_public_service()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(nameof(Secret_projection_and_domain_bindings_share_one_public_service)).Activate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        var resources = new TestResourceScope("public service bindings journey", timings: _timings);
        await resources.RunAsync(async () =>
        {
            using var secretPhase = _timings.Measure("e2e.public-bindings.secret");
            using var setupPhase = _timings.Measure("e2e.setup");
            var name = $"keys-{Guid.NewGuid():N}"[..18];
            var group = (await SendAsync(HttpMethod.Post, "/api/v1/resource-groups", new { name, description = "Secret browser test", tags = new[] { "e2e" } }, token)).GetProperty("id").GetString()!;
            resources.Defer("delete Secret browser group", cleanup => DeleteGroupAsync(group, cleanup));
            // Register this group before service cleanup: detach Secret, delete service, then reservation.
            var domainCleanup = resources.CreateCleanupSequence("public domain reservations");
            var consumerName = $"app-{Guid.NewGuid():N}"[..18];
            var consumer = (await SendAsync(HttpMethod.Post, "/api/v1/web-services", new
            {
                name = consumerName,
                resourceGroupId = group,
                exposure = "Public",
                tags = new[] { "e2e" },
                spec = new
                {
                    image = new { server = "docker.io", name = "library/nginx", tag = "1.27.5-alpine" },
                    config = new { environmentVariables = new Dictionary<string, string>(), portMappings = new[] { new { servicePort = 80, targetPort = "80", protocol = "TCP", name = "http" } } },
                    autoscaling = (object?)null,
                    probes = new { startup = Probe(), liveness = Probe(), readiness = Probe() }
                },
                placement = new { isolationScopeId = (string?)null, targets = new[] { new { deploymentTargetId = (string?)null, fixedReplicas = 1, computeProfileId = (string?)null } } }
            }, token)).GetProperty("webServiceId").GetString()!;
            resources.Defer("delete public binding consumer", cleanup => CleanupPublicServiceAsync(consumer, cleanup));
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
            setupPhase.Dispose();
            using var browserAcquisition = _timings.Measure("e2e.browser.acquire");
            var browserEndpoint = await fixture.GetBrowserEndpointAsync(token);
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(browserEndpoint, new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 }, IgnoreHTTPSErrors = true });
            await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
            browserAcquisition.Dispose();
            var page = await BrowserEvidence.NewPageAsync(context);
            await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
            await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
            await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true }).FillAsync(fixture.Alpha.Password);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
            await page.GetByLabel("Tenant").SelectOptionAsync(fixture.Alpha.DefaultTenantId);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Secrets", Exact = true }).ClickAsync();
            var content = page.Locator("#app-content");
            await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Secrets", Exact = true }).WaitForAsync();
            await CaptureSecretAsync(page, "catalog", "catalog", "Secrets");
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "New Secret", Exact = true }).ClickAsync();
            await content.GetByLabel("Resource group").SelectOptionAsync(group);
            await content.GetByLabel("Name", new LocatorGetByLabelOptions { Exact = true }).FillAsync(name);
            await content.GetByLabel("Purpose", new LocatorGetByLabelOptions { Exact = true }).FillAsync("Application credential projection");
            await content.GetByLabel("Key 1", new LocatorGetByLabelOptions { Exact = true }).FillAsync("password");
            var create = content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create Secret", Exact = true });
            await Assertions.Expect(create).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 20_000 });
            // All screenshots are captured before values are entered, or after the sensitive form unmounts.
            await CaptureSecretAsync(page, "create", "create", "Create Secret");
            var initialValue = $"test-{Guid.NewGuid():N}";
            await content.GetByLabel("Value 1").FillAsync(initialValue);
            var accepted = await page.RunAndWaitForResponseAsync(() => create.ClickAsync(), response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath.TrimEnd('/') == "/api/v1/secrets");
            Assert.Equal(201, accepted.Status);
            var requestHeaders = await accepted.Request.AllHeadersAsync();
            Assert.True(requestHeaders.TryGetValue("authorization", out var authorization) && authorization.StartsWith("Bearer ", StringComparison.Ordinal), "The browser write must use its real Keycloak bearer token.");
            var browserToken = authorization!["Bearer ".Length..];
            var secret = (await accepted.JsonAsync())!.Value.GetProperty("id").GetString()!;
            resources.Defer("detach and delete browser Secret", cleanup => CleanupSecretAsync(secret, cleanup));
            await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = name, Exact = true }).WaitForAsync();
            await AssertCaptureExcludesCredentialsAsync(page, secret, [initialValue, browserToken], token);
            await CaptureSecretAsync(page, "overview", secret, name);
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Bindings", Exact = true }).ClickAsync();
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Add binding", Exact = true }).ClickAsync();
            var groupInventory = await SendAsync(HttpMethod.Get, $"/api/v1/resource-groups/{group}/resources?pageNumber=1&pageSize=50", null, token);
            var consumerRegistration = groupInventory.GetProperty("items").EnumerateArray().Single(item =>
                item.GetProperty("resourceId").GetString() == consumer && item.GetProperty("resourceTypeId").GetString() == "pole/web-service");
            await content.GetByLabel("Consuming resource").SelectOptionAsync(consumerRegistration.GetProperty("registrationId").GetString()!);
            var bind = content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create binding", Exact = true });
            await Assertions.Expect(bind).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 20_000 });
            await bind.ClickAsync();
            var deployment = await WaitForProjectionAsync(secret, 1, "Ready", token);
            Assert.True(await fixture.KubernetesSecretExistsAsync(group, $"pole-secret-{secret}-{deployment}", token));
            await CaptureSecretAsync(page, "bindings", secret, name);
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Projections", Exact = true }).ClickAsync();
            await Assertions.Expect(content.GetByText("Projected to target", new LocatorGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await CaptureSecretAsync(page, "projections", secret, name);
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Rotate values", Exact = true }).ClickAsync();
            await CaptureSecretAsync(page, "rotate", secret, name);
            var replacementValue = $"test-{Guid.NewGuid():N}";
            await content.GetByLabel("Value 1").FillAsync(replacementValue);
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Review replacement", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Rotate Secret", Exact = true }).ClickAsync();
            await WaitForProjectionAsync(secret, 2, "Ready", token);
            await page.ReloadAsync();
            await Assertions.Expect(content.GetByLabel("Value 1")).ToHaveValueAsync("");
            await content.GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = "Bindings", Exact = true }).ClickAsync();
            await AssertCaptureExcludesCredentialsAsync(page, secret, [initialValue, replacementValue, browserToken], token);
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Detach binding", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Detach binding", Exact = true }).ClickAsync();
            await WaitForProjectionAsync(secret, 2, "Deleted", token);
            Assert.False(await fixture.KubernetesSecretExistsAsync(group, $"pole-secret-{secret}-{deployment}", token));
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete Secret", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Delete Secret", Exact = true }).ClickAsync();
            await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Secrets", Exact = true }).WaitForAsync();
            secretPhase.Dispose();
            using (_timings.Measure("e2e.public-bindings.domain"))
                await VerifyDomainBindingsAsync(page, domainCleanup, group, name, consumer, consumerName, token);
        });
    }

    private async Task AssertCaptureExcludesCredentialsAsync(IPage page, string id, string[] values, CancellationToken token)
    {
        var metadata = (await SendAsync(HttpMethod.Get, $"/api/v1/secrets/{id}", null, token)).GetRawText();
        var projections = (await SendAsync(HttpMethod.Get, $"/api/v1/secrets/{id}/projections", null, token)).GetRawText();
        var browserState = await page.EvaluateAsync<string>("JSON.stringify({ html: document.documentElement.outerHTML, inputs: Array.from(document.querySelectorAll('input,textarea')).map(x => x.value), local: {...localStorage}, session: {...sessionStorage} })");
        foreach (var value in values.Append(fixture.Alpha.AccessToken))
        {
            Assert.True(!metadata.Contains(value, StringComparison.Ordinal), "Secret metadata must exclude controlled credential values.");
            Assert.True(!projections.Contains(value, StringComparison.Ordinal), "Secret projection diagnostics must exclude controlled credential values.");
            Assert.True(!browserState.Contains(value, StringComparison.Ordinal), "Screenshot-bound DOM and workspace storage must exclude values after the sensitive form unmounts.");
        }
    }

    private static object Probe() => new
    {
        path = "/",
        port = "80",
        initialDelaySeconds = 0,
        periodSeconds = 3,
        timeoutSeconds = 1,
        successThreshold = 1,
        failureThreshold = 10
    };

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var request = fixture.CreateAuthenticatedRequest(method, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, token);
        Assert.True(response.IsSuccessStatusCode, $"Public bindings API {method} returned HTTP {(int)response.StatusCode}.");
        return response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<JsonElement>(token);
    }

    private async Task<string> WaitForProjectionAsync(string id, int version, string health, CancellationToken token)
    {
        using var timing = _timings.Measure("e2e.readiness.WaitForProjectionAsync");
        string? deploymentId = null;
        await TestWait.UntilAsync(async probeToken =>
        {
            var projections = await SendAsync(HttpMethod.Get, $"/api/v1/secrets/{id}/projections", null, probeToken);
            foreach (var row in projections.EnumerateArray())
                if (row.GetProperty("health").GetString() == health && row.GetProperty("desiredVersion").GetInt64() == version && (health == "Deleted" || row.GetProperty("appliedVersion").GetInt64() == version))
                {
                    deploymentId = row.GetProperty("resourceDeploymentId").GetString()!;
                    return true;
                }
            return false;
        }, TimeSpan.FromMinutes(3), "The Secret projection did not reach its expected version and health.", token, pollInterval: TimeSpan.FromSeconds(1));
        return deploymentId!;
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

    private async Task CleanupPublicServiceAsync(string id, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get,
            $"/api/v1/web-services/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        Assert.True(response.IsSuccessStatusCode, "Public consumer cleanup could not read its owned resource.");
        var service = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        foreach (var placement in service.GetProperty("deployments").EnumerateArray())
        {
            var deploymentId = Text(placement, "deploymentId");
            var path = $"/api/v1/web-services/{id}/deployments/{deploymentId}/hostnames";
            var bindings = await SendAsync(HttpMethod.Get, path, null, ct);
            foreach (var binding in bindings.EnumerateArray().Where(item => Text(item, "kind") == "custom"))
                await DeleteAsync($"{path}/{Text(binding, "id")}", ct);
        }
        await DeleteResourceAsync("web-services", id, ct);
    }

    private async Task DeleteResourceAsync(string route, string id, CancellationToken token)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteResourceAsync");
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
        }, TimeSpan.FromMinutes(1), "The Secret scenario resource did not finish deletion.", bounded.Token, pollInterval: TimeSpan.FromSeconds(1));
    }

    private async Task DeleteGroupAsync(string group, CancellationToken token)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteGroupAsync");
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/v1/resource-groups/{group}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, token);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound);
        await fixture.WaitForNamespaceDeletionAsync(group, token);
    }

    private async Task CaptureSecretAsync(IPage page, string section, string resourceId, string heading)
    {
        await BrowserWorkspaceReadiness.AssertAsync(page, resourceId, heading);
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

    private async Task VerifyDomainBindingsAsync(
        IPage page,
        TestResourceCleanupGroup domainCleanup,
        string groupId,
        string groupName,
        string appId,
        string appName,
        CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var zoneTechnicalName = $"zone-{suffix}";
        var aliasTechnicalName = $"api-{suffix}";
        var replacementTechnicalName = $"next-{suffix}";
        var service = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{appId}", null, ct);
        var placement = Assert.Single(service.GetProperty("deployments").EnumerateArray());
        var targetId = Text(placement, "deploymentTargetId");
        using (_timings.Measure("e2e.readiness.public-service"))
            await fixture.WaitForWebServiceReadyAsync(groupId, appName, ct);
        await page.GotoAsync(Url("domains", "catalog"));
        var content = page.Locator("#app-content");
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Reserve zone", Exact = true })
            .ClickAsync();
        var target = content.GetByLabel("Deployment target", new LocatorGetByLabelOptions { Exact = true });
        var eligibleTargets = await SendAsync(HttpMethod.Get, "/api/v1/deployment-targets/available?resourceType=pole%2Fweb-service&requiresPublicIngress=true", null, ct);
        Assert.Contains(eligibleTargets.EnumerateArray(), item => Text(item, "id") == targetId);
        await Assertions.Expect(target.Locator($"option[value='{targetId}']")).ToHaveCountAsync(1);
        await target.SelectOptionAsync(targetId);
        await content.GetByLabel("Technical name", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync(zoneTechnicalName);
        await content.GetByLabel("Zone label", new LocatorGetByLabelOptions { Exact = true }).FillAsync($"ui-{suffix}");
        await Assertions.Expect(content.GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = "Reserve zone", Exact = true })).ToBeEnabledAsync();
        await CaptureDomainAsync(page, "reserve", "create", "Reserve a domain zone");
        var reservation = await page.RunAndWaitForResponseAsync(() =>
                content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Reserve zone", Exact = true })
                    .ClickAsync(),
            response => response.Request.Method == "POST" &&
                        new Uri(response.Url).AbsolutePath.TrimEnd('/') == "/api/v1/domain-zones");
        Assert.Equal(201, reservation.Status);
        var zone = (await reservation.JsonAsync())!.Value;
        Assert.Equal(zoneTechnicalName, Text(zone, "technicalName"));
        Assert.Equal(targetId, Text(zone, "deploymentTargetId"));
        var zoneId = Text(zone, "id");
        var domain = Text(zone, "domain");
        domainCleanup.Defer("delete browser domain reservation",
            cleanup => DeleteAsync($"/api/v1/domain-zones/{zoneId}", cleanup));
        await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = domain, Exact = true })
            .WaitForAsync();
        await page.GotoAsync(Url("web-services", appId, "domains"));
        await content.GetByRole(AriaRole.Heading,
            new LocatorGetByRoleOptions { Name = "Domains and hostnames", Exact = true }).WaitForAsync();
        await content.GetByLabel("Reserved zone", new LocatorGetByLabelOptions { Exact = true })
            .SelectOptionAsync(zoneId);
        await content.GetByLabel("Binding technical name", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync(aliasTechnicalName);
        await content.GetByLabel("Hostname label", new LocatorGetByLabelOptions { Exact = true }).FillAsync("api");
        await Assertions.Expect(content.GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = "Attach alias", Exact = true })).ToBeEnabledAsync();
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
        await content.GetByText(aliasTechnicalName, new LocatorGetByTextOptions { Exact = true }).WaitForAsync();
        await CaptureDomainAsync(page, "service", appId, "Domains and hostnames");
        await page.GotoAsync(Url("domains", zoneId));
        await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = appName, Exact = true })
            .WaitForAsync();
        await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = groupName, Exact = true })
            .WaitForAsync();
        await Assertions
            .Expect(content.GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Remove reservation", Exact = true })).ToBeDisabledAsync();
        await CaptureDomainAsync(page, "ownership", zoneId, domain);
        await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = appName, Exact = true })
            .ClickAsync();
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Change alias", Exact = true })
            .ClickAsync();
        await content.GetByLabel("Binding technical name", new LocatorGetByLabelOptions { Exact = true })
            .FillAsync(replacementTechnicalName);
        await content.GetByLabel("Hostname label", new LocatorGetByLabelOptions { Exact = true }).FillAsync("next");
        await Assertions.Expect(content.GetByRole(AriaRole.Button,
            new LocatorGetByRoleOptions { Name = "Replace alias", Exact = true })).ToBeEnabledAsync();
        var detached = await page.RunAndWaitForResponseAsync(() =>
                content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Replace alias", Exact = true })
                    .ClickAsync(),
            response => response.Request.Method == "DELETE" &&
                        new Uri(response.Url).AbsolutePath.Contains("/hostnames/", StringComparison.Ordinal));
        Assert.Equal(202, detached.Status);
        await page.ReloadAsync();
        await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "next." + domain, Exact = true })
            .WaitForAsync();
        await content.GetByText(replacementTechnicalName, new LocatorGetByTextOptions { Exact = true })
            .WaitForAsync();
        Assert.Equal(0,
            await content.GetByText(aliasTechnicalName, new LocatorGetByTextOptions { Exact = true }).CountAsync());
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

    private async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteAsync");
        using var request =
            fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound,
            $"Domain cleanup returned HTTP {(int)response.StatusCode}.");
    }

    private static async Task CaptureDomainAsync(IPage page, string section, string resourceId, string heading)
    {
        await BrowserWorkspaceReadiness.AssertAsync(page, resourceId, heading);
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
