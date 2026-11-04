using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class DependencyBrowserE2ETests(PoleE2EFixture fixture)
{
    [SourceBuildFact]
    public async Task Application_dependencies_restore_partial_steps_and_allow_real_SQL_and_Redis_only_after_network_declarations()
    {
        using var testEvidence = new TestPhaseJournal(nameof(Application_dependencies_restore_partial_steps_and_allow_real_SQL_and_Redis_only_after_network_declarations)).Activate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var ct = timeout.Token;
        await fixture.EnsureSourceBuildInfrastructureReadyAsync(ct);
        var resources = new TestResourceScope("application dependency browser E2E");
        await resources.RunAsync(async () =>
        {
            resources.Defer("restore sample source revision", async cleanup =>
            {
                using var response = await fixture.Stub.PostAsJsonAsync("/__e2e/revision", new { revision = "v1" }, cleanup);
                response.EnsureSuccessStatusCode();
            });
            using (var revision = await fixture.Stub.PostAsJsonAsync("/__e2e/revision", new { revision = "dependencies" }, ct)) revision.EnsureSuccessStatusCode();
            var suffix = Guid.NewGuid().ToString("N")[..10];
            var groupName = $"deps-{suffix}";
            var group = await SendAsync(HttpMethod.Post, "/api/v1/resource-groups", new { name = groupName, description = "Dependency browser test", tags = new[] { "e2e" } }, ct);
            var groupId = Text(group, "id");
            resources.Defer("delete dependency group", cleanup => DeleteGroupAsync(groupId, cleanup));
            var profileId = await PrepareStorageAsync(ct);
            resources.Defer("disable dependency storage profile", cleanup => PlatformTestProfiles.DisableStorageAsync(fixture, profileId, cleanup));
            var pgName = $"pg-{suffix}";
            var pg = await SendAsync(HttpMethod.Post, "/api/v1/postgres", new
            {
                name = pgName,
                groupId,
                version = 17,
                instances = 1,
                storage = 1,
                storageProfileId = profileId,
                username = "application",
                password = $"test-{Guid.NewGuid():N}"
            }, ct);
            var pgId = Text(pg, "id");
            resources.Defer("delete dependency PostgreSQL", cleanup => DeleteResourceAsync("postgres", pgId, cleanup));
            var redisName = $"redis-{suffix}";
            var redis = await SendAsync(HttpMethod.Post, "/api/v1/redis-services", new
            {
                name = redisName,
                resourceGroupId = groupId,
                spec = new { storage = new { sizeGb = 1, storageProfileId = profileId } },
                tags = new[] { "e2e" }
            }, ct);
            var redisId = Text(redis, "redisId");
            resources.Defer("delete dependency Redis", cleanup => DeleteResourceAsync("redis-services", redisId, cleanup));
            var pgReady = await WaitDatabaseAsync("postgres", pgId, ct);
            var redisReady = await WaitDatabaseAsync("redis-services", redisId, ct);
            var pgEndpoint = pgReady.GetProperty("placement").GetProperty("endpoints")[0];
            var redisEndpoint = redisReady.GetProperty("placement").GetProperty("endpoints")[0];
            var literal = new Dictionary<string, string>
            {
                ["PGHOST"] = Text(pgEndpoint, "host"),
                ["PGPORT"] = pgEndpoint.GetProperty("port").GetInt32().ToString(),
                ["PGDATABASE"] = pgId,
                ["REDISHOST"] = Text(redisEndpoint, "host"),
                ["REDISPORT"] = redisEndpoint.GetProperty("port").GetInt32().ToString(),
                ["REDISDB"] = "0"
            };
            var appName = $"app-{suffix}";
            var app = await SendAsync(HttpMethod.Post, "/api/v1/web-services", new
            {
                name = appName,
                resourceGroupId = groupId,
                exposure = "Private",
                tags = new[] { "e2e" },
                spec = new
                {
                    image = new { server = "docker.io", name = "library/nginx", tag = "1.27" },
                    config = new { environmentVariables = literal, portMappings = new[] { new { servicePort = 80, targetPort = "8080", protocol = "TCP", name = "http" } } },
                    autoscaling = (object?)null,
                    probes = new { startup = Probe(), liveness = Probe(), readiness = Probe() }
                },
                placement = new { targets = new[] { new { fixedReplicas = 1 } } }
            }, ct);
            var appId = Text(app, "webServiceId");
            resources.Defer("delete dependency application", cleanup => DeleteResourceAsync("web-services", appId, cleanup));
            await fixture.WaitForNamespaceCreationAsync(groupId, ct);
            var secrets = await SendAsync(HttpMethod.Get, "/api/v1/secrets/?limit=50", null, ct);
            var credential = secrets.GetProperty("items").EnumerateArray().Single(item => item.TryGetProperty("managedBy", out var owner) && owner.ValueKind == JsonValueKind.Object && Text(owner, "resourceId") == pgId);
            var secretId = Text(credential, "id");
            await using var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
            await browserContainer.StartAsync(ct);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint, new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 }, IgnoreHTTPSErrors = true });
            var page = await BrowserEvidence.NewPageAsync(context);
            await LoginAsync(page);
            await page.GotoAsync(new Uri(fixture.FrontendBaseAddress, $"?view=web-services&resource={appId}&section=dependencies").ToString());
            var content = page.Locator("#app-content");
            await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Application dependencies", Exact = true }).WaitForAsync();
            await content.GetByLabel("Dependency Secret").SelectOptionAsync(secretId);
            var bind = content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Authorize Secret for all application deployments", Exact = true });
            await Assertions.Expect(bind).ToBeEnabledAsync();
            await bind.ClickAsync();
            await content.GetByText("This application has a saved binding for every current deployment.", new LocatorGetByTextOptions { Exact = true }).WaitForAsync();
            var usage = await SendAsync(HttpMethod.Get, $"/api/v1/secrets/{secretId}/usage", null, ct);
            var bindingId = usage.EnumerateArray().Single(item => Text(item.GetProperty("binding"), "consumerId") == appId).GetProperty("binding").GetProperty("id").GetString()!;
            resources.Defer("detach dependency credential sharing", cleanup => DeleteIfPresentAsync($"/api/v1/secrets/{secretId}/bindings/{bindingId}", cleanup));
            await AddSecretReferenceAsync(content, "PGUSER", secretId, "username");
            await AddSecretReferenceAsync(content, "PGPASSWORD", secretId, "password");
            await ApplyConfigurationAsync(page, content, appId);
            await page.ReloadAsync();
            await content.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = "Application dependencies", Exact = true }).WaitForAsync();
            await Assertions.Expect(content.GetByLabel("Environment name")).ToHaveCountAsync(2);
            Assert.Equal(new[] { "PGPASSWORD", "PGUSER" }, (await content.GetByLabel("Environment name").EvaluateAllAsync<string[]>("elements => elements.map(element => element.value)")).Order(StringComparer.Ordinal));
            await CaptureAsync(page, "secret-configured-network-missing", appId);

            var providerConnectionId = await EnsureGitHubAsync(ct);
            var analyzed = await SendAsync(HttpMethod.Post, $"/api/v1/git-provider-connections/{providerConnectionId}/repositories/9001/analysis", new { branch = "main" }, ct);
            var candidate = analyzed.GetProperty("candidates").EnumerateArray().First(item => Text(item, "dockerfilePreview").Contains("sdk:10.0", StringComparison.Ordinal));
            var source = await SendAsync(HttpMethod.Post, $"/api/v1/git-provider-connections/{providerConnectionId}/repositories/9001/links", new { branch = "main", candidateKey = Text(candidate, "candidateKey") }, ct);
            var sourceId = Text(source, "id");
            resources.Defer("unlink dependency probe source", cleanup => DeleteIfPresentAsync($"/api/v1/source-repositories/{sourceId}", cleanup));
            var configuration = await SendAsync(HttpMethod.Post, "/api/v1/source-build-configurations", new { sourceRepositoryId = sourceId, webServiceId = appId }, ct);
            var configurationId = Text(configuration, "id");
            resources.Defer("delete dependency probe build configuration", cleanup => DeleteIfPresentAsync($"/api/v1/source-build-configurations/{configurationId}", cleanup));
            var job = await SendAsync(HttpMethod.Post, $"/api/v1/source-build-configurations/{configurationId}/builds", new { }, ct);
            await WaitBuildAsync(Text(job, "id"), ct);
            await fixture.WaitForWebServiceReadyAsync(groupId, appName, ct);
            Assert.Equal(("ConnectionRejected", "ConnectionRejected"), await fixture.ProbeApplicationDependenciesAsync(groupId, appName, ct));

            await page.ReloadAsync();
            var groupInventory = await SendAsync(HttpMethod.Get, $"/api/v1/resource-groups/{groupId}/resources?pageNumber=1&pageSize=50", null, ct);
            var pgRegistrationId = Text(groupInventory.GetProperty("items").EnumerateArray().Single(item => Text(item, "resourceId") == pgId && Text(item, "resourceTypeId") == "pole/postgresql"), "registrationId");
            var redisRegistrationId = Text(groupInventory.GetProperty("items").EnumerateArray().Single(item => Text(item, "resourceId") == redisId && Text(item, "resourceTypeId") == "pole/redis"), "registrationId");
            var pgConnectionId = await DeclareNetworkAsync(page, content, appId, pgRegistrationId, "postgres", $"postgres-{suffix}");
            resources.Defer("delete PostgreSQL network declaration", cleanup => DeleteIfPresentAsync($"/api/v1/resource-connections/{pgConnectionId}", cleanup));
            await page.ReloadAsync(); // Persisted partial success must not create another PostgreSQL declaration.
            await content.GetByText($"postgres-{suffix}", new LocatorGetByTextOptions { Exact = true }).WaitForAsync();
            var redisConnectionId = await DeclareNetworkAsync(page, content, appId, redisRegistrationId, "redis", $"redis-{suffix}");
            resources.Defer("delete Redis network declaration", cleanup => DeleteIfPresentAsync($"/api/v1/resource-connections/{redisConnectionId}", cleanup));
            await WaitProbeAsync(groupId, appName, "Succeeded", "Succeeded", ct);
            // Remove the test's bootstrapped literal addresses before explicitly choosing provider-owned references.
            var latest = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{appId}", null, ct);
            var spec = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(latest.GetProperty("spec").GetRawText())!;
            spec["config"]!["environmentVariables"] = new System.Text.Json.Nodes.JsonObject();
            await SendAsync(HttpMethod.Put, $"/api/v1/web-services/{appId}", new { spec, tags = new[] { "e2e" }, exposure = "Private", expectedRevision = latest.GetProperty("activeRevision").GetInt64() }, ct);
            await page.ReloadAsync();
            await AddConnectionReferenceAsync(content, "PGHOST", pgConnectionId, "Host");
            await AddConnectionReferenceAsync(content, "PGPORT", pgConnectionId, "Port");
            await AddConnectionReferenceAsync(content, "PGDATABASE", pgConnectionId, "Configuration", "database");
            await AddConnectionReferenceAsync(content, "REDISHOST", redisConnectionId, "Host");
            await AddConnectionReferenceAsync(content, "REDISPORT", redisConnectionId, "Port");
            await AddConnectionReferenceAsync(content, "REDISDB", redisConnectionId, "Configuration", "database");
            await ApplyConfigurationAsync(page, content, appId);
            await WaitForLogicalEnvironmentAsync(groupId, appName, ct);
            await fixture.WaitForWebServiceReadyAsync(groupId, appName, ct);
            await WaitProbeAsync(groupId, appName, "Succeeded", "Succeeded", ct);
            await page.ReloadAsync();
            await Assertions.Expect(content.GetByLabel("Environment name")).ToHaveCountAsync(8);
            await CaptureAsync(page, "connected-logical-references", appId);
            await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Disconnect network access", Exact = true }).First.ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Disconnect network", Exact = true }).ClickAsync();
            await WaitProbeAsync(groupId, appName, "ConnectionRejected", "Succeeded", ct);
            Assert.Equal("Active", Text(await SendAsync(HttpMethod.Get, $"/api/v1/postgres/{pgId}", null, ct), "lifecycle"));
            Assert.Equal(secretId, Text(await SendAsync(HttpMethod.Get, $"/api/v1/secrets/{secretId}", null, ct), "id"));
        });
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(method, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, $"Dependency API {method} {path} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }
    private async Task DeleteIfPresentAsync(string path, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound, $"Dependency cleanup returned HTTP {(int)response.StatusCode}.");
    }
    private async Task DeleteResourceAsync(string route, string id, CancellationToken ct)
    {
        await DeleteIfPresentAsync($"/api/v1/{route}/{id}", ct);
        await TestWait.UntilAsync(async probeToken =>
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/{route}/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, probeToken);
            return TestDeletion.IsComplete(response.StatusCode);
        }, TimeSpan.FromMinutes(3), "The dependency resource did not finish deletion.", ct, pollInterval: TimeSpan.FromSeconds(2));
    }

    private async Task DeleteGroupAsync(string id, CancellationToken ct)
    {
        await TestWait.UntilAsync(async probeToken =>
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/v1/resource-groups/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, probeToken);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return true;
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            return false;
        }, TimeSpan.FromMinutes(3), "The dependency resource group did not finish cleanup.", ct, pollInterval: TimeSpan.FromSeconds(2));
        await fixture.WaitForNamespaceDeletionAsync(id, ct);
    }

    private async Task<JsonElement> WaitDatabaseAsync(string route, string id, CancellationToken ct)
    {
        JsonElement result = default;
        await TestWait.UntilAsync(async probeToken =>
        {
            result = await SendAsync(HttpMethod.Get, $"/api/v1/{route}/{id}", null, probeToken);
            var placement = result.GetProperty("placement");
            return Text(placement, "status") == "Applied" &&
                placement.GetProperty("observedGeneration").GetInt64() == placement.GetProperty("desiredGeneration").GetInt64();
        }, TimeSpan.FromMinutes(3), "The dependency database did not reach Applied.", ct, pollInterval: TimeSpan.FromSeconds(2));
        return result;
    }

    private Task WaitBuildAsync(string id, CancellationToken ct) =>
        TestWait.UntilAsync(async probeToken =>
        {
            var job = await SendAsync(HttpMethod.Get, $"/api/v1/source-build-jobs/{id}", null, probeToken);
            var status = Text(job.GetProperty("state"), "status");
            Assert.NotEqual("failed", status);
            return status == "succeeded";
        }, TimeSpan.FromMinutes(8), "The dependency probe source build did not complete.", ct, pollInterval: TimeSpan.FromSeconds(2));

    private Task WaitProbeAsync(string group, string app, string postgres, string redis, CancellationToken ct) =>
        TestWait.UntilAsync(async probeToken => await fixture.ProbeApplicationDependenciesAsync(group, app, probeToken) == (postgres, redis),
            TimeSpan.FromMinutes(3), "The application did not report the expected PostgreSQL and Redis operation outcomes.", ct,
            pollInterval: TimeSpan.FromSeconds(2));

    private Task WaitForLogicalEnvironmentAsync(string group, string app, CancellationToken ct)
    {
        var expected = new[] { "PGDATABASE", "PGHOST", "PGPASSWORD", "PGPORT", "PGUSER", "REDISDB", "REDISHOST", "REDISPORT" };
        return TestWait.UntilAsync(async probeToken =>
        {
            var deployment = await fixture.GetWebServiceDeploymentAsync(group, app, probeToken);
            var container = deployment.GetProperty("spec").GetProperty("template").GetProperty("spec").GetProperty("containers")[0];
            if (!container.TryGetProperty("env", out var environment) || !environment.EnumerateArray().Select(item => Text(item, "name")).Order(StringComparer.Ordinal).SequenceEqual(expected)) return false;
            Assert.All(environment.EnumerateArray().Where(item => Text(item, "name") is "PGUSER" or "PGPASSWORD"), item => Assert.True(item.GetProperty("valueFrom").TryGetProperty("secretKeyRef", out _)));
            return true;
        }, TimeSpan.FromMinutes(3), "The application deployment did not receive all logical dependency references.", ct,
            pollInterval: TimeSpan.FromSeconds(2));
    }

    private async Task<string> PrepareStorageAsync(CancellationToken ct)
    {
        async Task<JsonElement> Admin(HttpMethod method, string path, object? body = null)
        {
            using var request = fixture.CreatePlatformAdminRequest(method, path);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await fixture.Api.SendAsync(request, ct); response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        var targets = await Admin(HttpMethod.Get, "/api/v1/platform/deployment-targets/");
        var targetId = Text(targets.EnumerateArray().Single(target => target.GetProperty("isDefault").GetBoolean()), "id");
        var classes = await Admin(HttpMethod.Get, $"/api/v1/platform/storage/storage-classes?deploymentTargetId={targetId}");
        var profile = await Admin(HttpMethod.Post, "/api/v1/platform/storage/profiles", new
        {
            technicalName = $"disk-{Guid.NewGuid():N}",
            displayName = "Dependency test disk",
            description = "Ephemeral test profile",
            mediaType = "Network",
            performanceTier = "Standard",
            isEnabled = true,
            isDefault = false,
            targetMappings = new[] { new { deploymentTargetId = targetId, storageClassName = Text(classes.EnumerateArray().First(), "name") } },
            allowedResourceTypes = new[] { "pole/postgresql", "pole/redis" },
            tenantAccess = new { mode = "SelectedTenants", tenantIds = new[] { fixture.Alpha.DefaultTenantId } }
        });
        return Text(profile, "id");
    }
    private async Task<string> EnsureGitHubAsync(CancellationToken ct)
    {
        var existing = await SendAsync(HttpMethod.Get, "/api/v1/git-provider-connections", null, ct);
        if (existing.GetArrayLength() > 0) return Text(existing[0], "id");
        var begin = await SendAsync(HttpMethod.Post, "/api/v1/git-provider-connections/github/install",
            new { TechnicalName = "github-e2e" }, ct);
        using var callback = await fixture.Stub.GetAsync(Text(begin, "installUrl"), ct); callback.EnsureSuccessStatusCode();
        return Text((await SendAsync(HttpMethod.Get, "/api/v1/git-provider-connections", null, ct))[0], "id");
    }
    private async Task LoginAsync(IPage page)
    {
        await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
        await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
        await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true }).FillAsync(fixture.Alpha.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
        await page.GetByLabel("Tenant").SelectOptionAsync(fixture.Alpha.DefaultTenantId);
    }
    private static async Task AddSecretReferenceAsync(ILocator content, string name, string secret, string key)
    {
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Add Secret reference", Exact = true }).ClickAsync();
        await content.GetByLabel("Environment name", new LocatorGetByLabelOptions { Exact = true }).Last.FillAsync(name);
        await content.GetByLabel("Saved Secret").Last.SelectOptionAsync(secret);
        await content.GetByLabel("Source Secret key").Last.SelectOptionAsync(key);
    }
    private static async Task AddConnectionReferenceAsync(ILocator content, string name, string connection, string kind, string? field = null)
    {
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Add connection reference", Exact = true }).ClickAsync();
        await content.GetByLabel("Environment name", new LocatorGetByLabelOptions { Exact = true }).Last.FillAsync(name);
        await content.GetByLabel("Saved network connection").Last.SelectOptionAsync(connection);
        await content.GetByLabel("Connection field").Last.SelectOptionAsync(kind);
        if (field is not null) await content.GetByLabel("Provider configuration field").Last.SelectOptionAsync(field);
    }
    private static async Task ApplyConfigurationAsync(IPage page, ILocator content, string app)
    {
        await content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Apply dependency configuration", Exact = true }).ClickAsync();
        var response = await page.RunAndWaitForResponseAsync(() => page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Apply configuration", Exact = true }).ClickAsync(),
            response => response.Request.Method == "PUT" && new Uri(response.Url).AbsolutePath == $"/api/v1/web-services/{app}");
        Assert.Equal(202, response.Status);
    }
    private static async Task<string> DeclareNetworkAsync(IPage page, ILocator content, string app, string destinationRegistrationId, string endpoint, string name)
    {
        await content.GetByLabel("Destination resource", new LocatorGetByLabelOptions { Exact = true }).SelectOptionAsync(destinationRegistrationId);
        await content.GetByLabel("Connection endpoint").SelectOptionAsync(endpoint);
        await content.GetByLabel("Connection name").FillAsync(name);
        var create = content.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Create network access", Exact = true });
        await Assertions.Expect(create).ToBeEnabledAsync();
        var response = await page.RunAndWaitForResponseAsync(() => create.ClickAsync(), response => response.Request.Method == "POST" && new Uri(response.Url).AbsolutePath.TrimEnd('/') == "/api/v1/resource-connections");
        Assert.Equal(202, response.Status);
        return Text((await response.JsonAsync())!.Value, "id");
    }
    private static async Task CaptureAsync(IPage page, string section, string resourceId)
    {
        await BrowserWorkspaceReadiness.AssertAsync(page, resourceId, "Application dependencies", "Dependencies");
        var directory = Path.GetFullPath(Path.Combine(".artifacts", "frontend-dependencies")); Directory.CreateDirectory(directory);
        foreach (var (viewport, width, height) in new[] { ("desktop", 1440, 900), ("mobile", 390, 844) })
        {
            await page.SetViewportSizeAsync(width, height);
            foreach (var theme in new[] { "light", "dark" })
            {
                if (await page.Locator("html").GetAttributeAsync("data-theme") != theme)
                    await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Switch to {theme} theme", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(directory, $"dependencies-{viewport}-{section}-{theme}.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"), "The dependency workspace overflows its viewport.");
            }
        }
        await page.SetViewportSizeAsync(1440, 900);
    }
    private static object Probe() => new { path = "/health", port = "8080", initialDelaySeconds = 1, periodSeconds = 2, timeoutSeconds = 1, failureThreshold = 30, successThreshold = 1 };
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString()!;
}
