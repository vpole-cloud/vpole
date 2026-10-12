using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Pole.Features.Realtime.Contracts;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class RealtimeBrowserE2ETests(
    PoleE2EFixture fixture,
    ITestOutputHelper output)
{
    [Fact]
    public async Task Real_Keycloak_cookie_and_notifications_are_shared_by_two_full_API_processes()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = budget.Token;
        await using var resources = new TestResourceScope("realtime root replicas E2E");
        resources.Defer("stop realtime API replica", fixture.StopRealtimeReplicaAsync);
        var replicaAddress = await fixture.StartRealtimeReplicaAsync(ct);
        var owned = await CreateOwnedServiceAsync(resources, ct);
        await WaitForReleaseAsync(owned.Id, 1, "Succeeded", ct);
        var origin = fixture.FrontendBaseAddress.GetLeftPart(UriPartial.Authority);
        var cookies = new CookieContainer();
        var oldCookies = new CookieContainer();
        using (var bootstrap = fixture.CreateAuthenticatedRequest(HttpMethod.Post,
                   "/api/v1/realtime/session", fixture.Alpha))
        {
            bootstrap.Headers.Add("Origin", origin);
            using var response = await fixture.Api.SendAsync(bootstrap, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            foreach (var cookie in response.Headers.GetValues("Set-Cookie"))
            {
                cookies.SetCookies(fixture.ApiBaseAddress, cookie);
                oldCookies.SetCookies(fixture.ApiBaseAddress, cookie);
            }
        }
        await using var first = ReplicaClient(fixture.ApiBaseAddress, cookies, origin);
        await using var second = ReplicaClient(replicaAddress, cookies, origin);
        var firstChange = new TaskCompletionSource<RealtimeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondChange = new TaskCompletionSource<RealtimeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRevoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRevoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.On<RealtimeChange>("Changed", change => { if (change.ResourceId == owned.Id && change.Revision == 2) firstChange.TrySetResult(change); });
        second.On<RealtimeChange>("Changed", change => { if (change.ResourceId == owned.Id && change.Revision == 2) secondChange.TrySetResult(change); });
        first.On("SessionRevoked", () => firstRevoked.TrySetResult());
        second.On("SessionRevoked", () => secondRevoked.TrySetResult());
        await first.StartAsync(ct);
        await second.StartAsync(ct);
        var interests = new[] { new RealtimeSubscriptionRequest("web-services", fixture.Alpha.DefaultTenantId, owned.Id) };
        foreach (var client in new[] { first, second })
        {
            var acknowledgement = Assert.Single(await client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe", [interests], ct));
            Assert.True(acknowledgement.Covered);
            Assert.Equal(acknowledgement.SubscriptionId,
                Assert.Single(await client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Heartbeat", [], ct)).SubscriptionId);
        }
        await Assert.ThrowsAsync<HubException>(() => second.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe",
            [new[] { new RealtimeSubscriptionRequest("web-services", fixture.Beta.DefaultTenantId) }], ct));
        Assert.Equal(2, await UpdateMetadataAsync(owned.Id, 1, "replica", ct, replicaAddress));
        await Task.WhenAll(firstChange.Task, secondChange.Task).WaitAsync(TimeSpan.FromSeconds(15), ct);
        var current = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{owned.Id}", null, ct);
        Assert.Equal(2, current.GetProperty("activeRevision").GetInt64());
        using var transport = new HttpClient(new HttpClientHandler { CookieContainer = cookies });
        transport.DefaultRequestHeaders.Add("Origin", origin);
        using var deleted = await transport.DeleteAsync(new Uri(replicaAddress, "/api/v1/realtime/session"), ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        // B initiates logout: notification of A's connection necessarily crosses the backplane.
        await Task.WhenAll(firstRevoked.Task, secondRevoked.Task).WaitAsync(TimeSpan.FromSeconds(15), ct);
        using var savedTransport = new HttpClient(new HttpClientHandler { CookieContainer = oldCookies });
        savedTransport.DefaultRequestHeaders.Add("Origin", origin);
        using var rejected = await savedTransport.PostAsync(new Uri(fixture.ApiBaseAddress,
            "/api/v1/realtime/hub/negotiate?negotiateVersion=1"), null, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    private static HubConnection ReplicaClient(Uri address, CookieContainer cookies, string origin) => new HubConnectionBuilder()
        .WithUrl(new Uri(address, "/api/v1/realtime/hub"), options =>
        {
            options.Cookies = cookies;
            options.Headers["Origin"] = origin;
            options.Transports = HttpTransportType.WebSockets;
        }).Build();

    [Fact]
    public async Task Ui_token_refresh_renews_cookie_transport_and_preserves_dirty_tenant_workspace()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = budget.Token;
        await using var resources = new TestResourceScope("realtime browser refresh E2E");
        var owned = await CreateOwnedServiceAsync(resources, ct);
        await fixture.WaitForNamespaceCreationAsync(owned.GroupId, ct);
        await fixture.WaitForWebServiceReadyAsync(owned.GroupId, owned.Name, ct);
        await fixture.UseInteractiveClientTokenLifespanAsync(resources, TimeSpan.FromSeconds(40), ct);

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
        var journal = new BrowserJournal(page, fixture.Alpha.DefaultTenantId);
        await LoginAsync(page);
        await page.GotoAsync(ResourceUrl(owned.Id, "configuration"));
        await Assertions.Expect(page.GetByRole(AriaRole.Heading,
            new PageGetByRoleOptions { Name = "Service configuration", Exact = true })).ToBeVisibleAsync();
        await WaitUntilAsync(() => journal.Bootstraps.Count > 0 && journal.HasCoverage(owned.Id),
            TimeSpan.FromSeconds(30), "The real browser did not acknowledge pilot coverage.", ct);
        var originalBootstrap = journal.Bootstraps.Last();
        var originalConnection = journal.LatestCoveredConnection(owned.Id);
        var refreshesBeforeDraft = journal.RefreshResponses;
        var draft = "REALTIME_REFRESH_DRAFT=unsaved";
        await page.GetByLabel("Environment variables").FillAsync(draft);
        var workspaceTabs = page.GetByRole(AriaRole.Tablist,
            new PageGetByRoleOptions { Name = "Web service workspace", Exact = true });
        var workspaceTabCount = await workspaceTabs.GetByRole(AriaRole.Tab).CountAsync();
        var selectedResource = workspaceTabs.GetByRole(AriaRole.Tab,
            new LocatorGetByRoleOptions { Name = owned.Name, Exact = false });
        await Assertions.Expect(selectedResource).ToHaveAttributeAsync("aria-selected", "true");
        await AssertNoReadableCredentialsAsync(page, journal);

        // Observe Keycloak's own refresh grant, not a manual updateToken call or an injected token.
        await WaitUntilAsync(() => journal.RefreshResponses > refreshesBeforeDraft,
            TimeSpan.FromSeconds(75), "The interactive client did not complete a real refresh_token grant.", ct);
        var refreshCompleted = journal.LastRefreshTimestamp;
        await WaitUntilAsync(() =>
        {
            var bootstrap = journal.Bootstraps.LastOrDefault(value => value.Timestamp > refreshCompleted &&
                value.ExpiresAtUtc > originalBootstrap.ExpiresAtUtc);
            return bootstrap is not null && journal.LatestCoveredConnection(owned.Id, bootstrap.Timestamp) > originalConnection;
        },
            TimeSpan.FromSeconds(30), "Refreshed credentials did not bootstrap and acknowledge a new cookie connection.", ct);

        Assert.Equal(fixture.Alpha.DefaultTenantId, await page.GetByLabel("Tenant").InputValueAsync());
        Assert.Equal(workspaceTabCount, await workspaceTabs.GetByRole(AriaRole.Tab).CountAsync());
        await Assertions.Expect(selectedResource).ToHaveAttributeAsync("aria-selected", "true");
        await Assertions.Expect(page.GetByRole(AriaRole.Tab,
            new PageGetByRoleOptions { Name = "Configuration", Exact = true })).ToHaveAttributeAsync("aria-selected", "true");
        Assert.Equal(draft, await page.GetByLabel("Environment variables").InputValueAsync());
        await AssertNoReadableCredentialsAsync(page, journal);
        Assert.True(journal.HandshakeHeadersSafe, "Realtime bootstrap and hub requests must not contain a tenant header or hub bearer authentication.");
        await journal.DrainAsync();
        Assert.Equal(0, journal.ParseFailures);
    }

    [Fact]
    public async Task Covered_pending_release_reduces_metadata_reads_against_real_disconnected_polling()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = budget.Token;
        await using var resources = new TestResourceScope("realtime browser polling E2E");
        var owned = await CreateOwnedServiceAsync(resources, ct);
        await fixture.WaitForNamespaceCreationAsync(owned.GroupId, ct);
        await WaitForReleaseAsync(owned.Id, 1, "Succeeded", ct);
        // The target belongs to this disposable fixture's Mongo/API graph. This follows the
        // ReleaseBrowserE2ETests pattern; the shared lab and its credentials remain untouched.
        resources.Defer("restore isolated realtime target availability", cleanup => SetTargetEnabledAsync(owned.TargetId, true, cleanup));
        await SetTargetEnabledAsync(owned.TargetId, false, ct);
        await SendAsync(HttpMethod.Post, $"/api/v1/web-services/{owned.Id}/redeploy?expectedRevision=1", null, ct);
        await WaitUntilAsync(async () =>
        {
            var value = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{owned.Id}", null, ct);
            var placement = value.GetProperty("deployments")[0];
            return Text(placement, "status") == "Failed" && Text(placement, "failureCode") == "DeploymentTargetDisabled";
        }, TimeSpan.FromMinutes(2), "The real pending release did not reach its controlled disabled-target state.", ct);
        await fixture.UseInteractiveClientTokenLifespanAsync(resources, TimeSpan.FromMinutes(5), ct);

        await using var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
        await browserContainer.StartAsync(ct);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint,
            new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
        const int windowSeconds = 65;
        var baseline = await MeasurePendingReadsAsync(browser, owned, disconnected: true, windowSeconds, ct);
        var covered = await MeasurePendingReadsAsync(browser, owned, disconnected: false, windowSeconds, ct);
        Assert.True(baseline >= 20, "The pending-release fallback must establish an actual 2.5-second polling baseline.");
        var reduction = 1d - (double)covered / baseline;
        output.WriteLine($"Controlled pending release: {windowSeconds}s per window; fallback GETs={baseline}; covered GETs={covered}; reduction={reduction:P1}.");
        output.WriteLine("Ordinary 20s polling versus the 60s safety interval has a theoretical 66.7% steady-state reduction; this 80% gate is specific to the controlled 2.5s pending-release pilot.");
        Assert.True(reduction >= 0.8, "Acknowledged pilot coverage must reduce controlled pending-release metadata GETs by at least 80% against its measured disconnected baseline.");
        await SetTargetEnabledAsync(owned.TargetId, true, ct);
        await WaitForReleaseAsync(owned.Id, 2, "Succeeded", ct);
    }

    [Fact]
    public async Task Committed_metadata_bursts_keep_a_slow_read_alive_and_reach_visible_revisions_within_budget()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = budget.Token;
        await using var resources = new TestResourceScope("realtime browser metadata E2E");
        var owned = await CreateOwnedServiceAsync(resources, ct);
        await fixture.WaitForNamespaceCreationAsync(owned.GroupId, ct);
        await fixture.WaitForWebServiceReadyAsync(owned.GroupId, owned.Name, ct);
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
        var journal = new BrowserJournal(page, fixture.Alpha.DefaultTenantId);
        await LoginAsync(page);
        await page.GotoAsync(ResourceUrl(owned.Id, "overview"));
        var visibleRevision = page.Locator(".web-service-overview dl div")
            .Filter(new LocatorFilterOptions { HasText = "Active revision" }).Locator("dd");
        await Assertions.Expect(visibleRevision).ToHaveTextAsync("1");
        await WaitUntilAsync(() => journal.HasCoverage(owned.Id), TimeSpan.FromSeconds(30),
            "The metadata pilot did not acknowledge real coverage.", ct);
        var detailPath = $"/api/v1/web-services/{owned.Id}";
        var acknowledgedAt = Stopwatch.GetTimestamp();
        try
        {
            await WaitUntilAsync(() => Stopwatch.GetElapsedTime(acknowledgedAt) >= TimeSpan.FromMilliseconds(500) &&
                journal.PilotReadsQuiet(detailPath, TimeSpan.FromMilliseconds(500)), TimeSpan.FromSeconds(10),
                "Initial metadata subscriptions and their HTTP resync did not settle.", ct);
        }
        catch (TimeoutException)
        {
            output.WriteLine(journal.DescribePilotReads(detailPath));
            throw;
        }
        var detailRoute = $"**{detailPath}";
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var canceled = 0;
        page.RequestFailed += (_, request) =>
        {
            if (request.Method == "GET" && new Uri(request.Url).AbsolutePath == detailPath)
                Interlocked.Increment(ref canceled);
        };
        await page.RouteAsync(detailRoute, async route =>
        {
            if (route.Request.Method != "GET") { await route.ContinueAsync(); return; }
            if (Interlocked.Increment(ref reads) != 1) { await route.ContinueAsync(); return; }
            var realResponse = await route.FetchAsync();
            Assert.True(realResponse.Ok, "The delayed snapshot must come from the real authenticated API.");
            held.TrySetResult();
            await release.Task;
            try { await route.FulfillAsync(new RouteFulfillOptions { Response = realResponse }); }
            catch (PlaywrightException) { Interlocked.Increment(ref canceled); }
            finally { await realResponse.DisposeAsync(); delivered.TrySetResult(); }
        });
        long revision = 1;
        try
        {
            revision = await UpdateMetadataAsync(owned.Id, revision, "burst-0", ct);
            await held.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            for (var index = 1; index < 8; index++)
                revision = await UpdateMetadataAsync(owned.Id, revision, $"burst-{index}", ct);
            await WaitForReleaseAsync(owned.Id, revision, "Succeeded", ct);
            await WaitUntilAsync(() => journal.LastRevision(owned.Id) >= revision && journal.IsQuiet(owned.Id, TimeSpan.FromSeconds(2)),
                TimeSpan.FromSeconds(20), "The committed metadata burst did not reach the real browser before releasing its old GET.", ct);
            Assert.Equal(1, Volatile.Read(ref reads));
            Assert.Equal(0, Volatile.Read(ref canceled));
            release.TrySetResult();
            await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await Assertions.Expect(visibleRevision).ToHaveTextAsync(revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new LocatorAssertionsToHaveTextOptions { Timeout = 10_000 });
            await WaitUntilAsync(() => journal.IsQuiet(owned.Id, TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(5),
                "The trailing metadata read did not settle.", ct);
            Assert.Equal(2, Volatile.Read(ref reads));
            Assert.Equal(0, Volatile.Read(ref canceled));
        }
        finally
        {
            release.TrySetResult();
            await page.UnrouteAsync(detailRoute);
        }

        var samples = new List<double>();
        for (var index = 0; index < 20; index++)
        {
            // Starting before the write includes its request/commit latency and therefore bounds
            // commit-to-visible-revision latency conservatively without a product timing hook.
            var started = Stopwatch.GetTimestamp();
            revision = await UpdateMetadataAsync(owned.Id, revision, $"latency-{index}", ct);
            await Assertions.Expect(visibleRevision).ToHaveTextAsync(revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new LocatorAssertionsToHaveTextOptions { Timeout = 10_000 });
            samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        samples.Sort();
        var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
        output.WriteLine($"Committed WS metadata: {samples.Count} write-start-to-visible-revision samples; p95={p95:F0}ms (includes write request and commit).");
        Assert.True(p95 <= 3000, "The sampled metadata commit-to-visible-revision p95 upper bound must be at most three seconds.");
        await AssertNoReadableCredentialsAsync(page, journal);
        await journal.DrainAsync();
        Assert.Equal(0, journal.ParseFailures);
    }

    private async Task<int> MeasurePendingReadsAsync(IBrowser browser, OwnedService owned, bool disconnected, int windowSeconds, CancellationToken ct)
    {
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            IgnoreHTTPSErrors = true
        });
        var page = await context.NewPageAsync();
        if (disconnected)
            await page.RouteAsync("**/api/v1/realtime/**", route => route.AbortAsync("failed"));
        var journal = new BrowserJournal(page, fixture.Alpha.DefaultTenantId);
        await LoginAsync(page);
        await page.GotoAsync(ResourceUrl(owned.Id, "releases"));
        var pendingRelease = page.Locator("[aria-label='Release revision 2']").GetByTestId("release-status");
        await Assertions.Expect(pendingRelease).ToHaveTextAsync("Pending");
        if (!disconnected)
            await WaitUntilAsync(() => journal.HasCoverage(owned.Id), TimeSpan.FromSeconds(30),
                "The covered pending-release sample did not acknowledge subscriptions.", ct);
        // Wait for initial reads and the subscribe acknowledgement's resync outside the window.
        var detailPath = $"/api/v1/web-services/{owned.Id}";
        var acknowledgedAt = Stopwatch.GetTimestamp();
        await WaitUntilAsync(() => Stopwatch.GetElapsedTime(acknowledgedAt) >= TimeSpan.FromMilliseconds(500) &&
            journal.PilotReadsQuiet(detailPath, TimeSpan.FromMilliseconds(500)),
            TimeSpan.FromSeconds(10), "Initial pending-release metadata reads did not settle.", ct);
        var started = Stopwatch.GetTimestamp();
        var connectionClosures = journal.ConnectionClosures;
        var end = started + windowSeconds * Stopwatch.Frequency;
        await WaitUntilAsync(() => Stopwatch.GetTimestamp() >= end, TimeSpan.FromSeconds(windowSeconds + 5),
            "The metadata sampling window did not finish.", ct);
        var finished = Stopwatch.GetTimestamp();
        await Assertions.Expect(pendingRelease).ToHaveTextAsync("Pending");
        if (disconnected) Assert.False(journal.HasCoverage(owned.Id));
        else
        {
            Assert.True(journal.HasCoverage(owned.Id), "Realtime coverage must remain confirmed throughout the sample.");
            output.WriteLine($"Covered window: bootstraps={journal.Bootstraps.Count}; closures before={connectionClosures}; closures after={journal.ConnectionClosures}; refresh grants={journal.RefreshResponses}.");
            Assert.Equal(connectionClosures, journal.ConnectionClosures);
        }
        var count = journal.CountPilotReads(detailPath, started, finished);
        Assert.Equal(0, journal.MetadataReadFailures);
        await AssertNoReadableCredentialsAsync(page, journal);
        await journal.DrainAsync();
        Assert.Equal(0, journal.ParseFailures);
        return count;
    }

    private async Task<long> UpdateMetadataAsync(string id, long expectedRevision, string marker, CancellationToken ct, Uri? apiAddress = null)
    {
        var path = $"/api/v1/web-services/{id}";
        var accepted = await SendAsync(HttpMethod.Put, apiAddress is null ? path : new Uri(apiAddress, path).ToString(),
            new { spec = Spec("initial"), tags = new[] { "e2e", marker }, exposure = "Private", expectedRevision }, ct);
        return accepted.GetProperty("resourceRevision").GetInt64();
    }

    private async Task WaitForReleaseAsync(string id, long revision, string outcome, CancellationToken ct)
    {
        await WaitUntilAsync(async () =>
        {
            var history = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{id}/releases", null, ct);
            return history.GetProperty("releases").GetProperty("items").EnumerateArray().Any(value =>
                value.GetProperty("resourceRevision").GetInt64() == revision && Text(value, "outcome") == outcome);
        }, TimeSpan.FromMinutes(4), "The owned realtime release did not reach its required outcome.", ct);
    }

    private async Task SetTargetEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        using var get = fixture.CreatePlatformAdminRequest(HttpMethod.Get, $"/api/v1/platform/deployment-targets/{id}");
        using var result = await fixture.Api.SendAsync(get, ct);
        Assert.True(result.IsSuccessStatusCode, $"Realtime target read returned HTTP {(int)result.StatusCode}.");
        var target = await result.Content.ReadFromJsonAsync<JsonElement>(ct);
        using var put = fixture.CreatePlatformAdminRequest(HttpMethod.Put, $"/api/v1/platform/deployment-targets/{id}");
        put.Content = JsonContent.Create(new
        {
            displayName = Text(target, "displayName"),
            enabled,
            acceptsWorkloads = enabled,
            workloadConfiguration = target.GetProperty("workloadConfiguration"),
            revision = target.GetProperty("revision").GetInt64()
        });
        using var response = await fixture.Api.SendAsync(put, ct);
        Assert.True(response.IsSuccessStatusCode, $"Realtime target update returned HTTP {(int)response.StatusCode}.");
    }

    private async Task<OwnedService> CreateOwnedServiceAsync(TestResourceScope resources, CancellationToken ct)
    {
        var name = $"realtime-{Guid.NewGuid():N}"[..20];
        var group = await SendAsync(HttpMethod.Post, "/api/v1/resource-groups",
            new { name, description = "Owned realtime browser acceptance", tags = new[] { "e2e" } }, ct);
        var groupId = Text(group, "id");
        resources.Defer("delete realtime group and owned namespace", cleanup => DeleteGroupAsync(groupId, cleanup));
        var targets = await SendAsync(HttpMethod.Get,
            "/api/v1/deployment-targets/available?resourceType=pole%2Fweb-service&requiresPublicIngress=false", null, ct);
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
        resources.Defer("delete realtime service", cleanup => DeleteServiceAsync(id, cleanup));
        return new OwnedService(id, groupId, targetId, name);
    }

    private async Task LoginAsync(IPage page)
    {
        await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
        await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
        await page.GetByRole(AriaRole.Textbox,
            new PageGetByRoleOptions { Name = "Password", Exact = true }).FillAsync(fixture.Alpha.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
        await page.GetByLabel("Tenant").SelectOptionAsync(fixture.Alpha.DefaultTenantId);
    }

    private string ResourceUrl(string id, string section) => new Uri(fixture.FrontendBaseAddress,
        $"?view=web-services&resource={id}&section={section}&tenant={fixture.Alpha.DefaultTenantId}").ToString();

    private static object Spec(string marker)
    {
        var probe = new { path = "/", port = "80", initialDelaySeconds = 0, periodSeconds = 2, timeoutSeconds = 1, failureThreshold = 30, successThreshold = 1 };
        return new
        {
            image = new { server = "docker.io", name = "library/nginx", tag = "1.27.5-alpine" },
            config = new { environmentVariables = new Dictionary<string, string> { ["REALTIME_MARKER"] = marker }, portMappings = new[] { new { servicePort = 80, targetPort = "80", protocol = "TCP", name = "http" } } },
            autoscaling = (object?)null,
            probes = new { startup = probe, liveness = probe, readiness = probe }
        };
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(method, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, $"Realtime browser API {method} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task DeleteServiceAsync(string id, CancellationToken ct)
    {
        await DeleteAsync($"/api/v1/web-services/{id}", ct);
        await WaitUntilAsync(async () =>
        {
            using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, $"/api/v1/web-services/{id}", fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(request, ct);
            return response.StatusCode == HttpStatusCode.NotFound;
        }, TimeSpan.FromMinutes(3), "The owned realtime service did not complete cleanup.", ct);
    }

    private async Task DeleteGroupAsync(string id, CancellationToken ct)
    {
        await DeleteAsync($"/api/v1/resource-groups/{id}", ct);
        await fixture.WaitForNamespaceDeletionAsync(id, ct);
    }

    private async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound,
            $"Realtime browser cleanup returned HTTP {(int)response.StatusCode}.");
    }

    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;
    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message, CancellationToken ct) =>
        WaitUntilAsync(() => Task.FromResult(condition()), timeout, message, ct);
    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string message, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await condition()) return;
            await Task.Delay(100, ct);
        }
        throw new TimeoutException(message);
    }

    private static async Task AssertNoReadableCredentialsAsync(IPage page, BrowserJournal journal)
    {
        // Only a boolean crosses the browser boundary; no token/cookie/storage value is exported.
        var safe = await page.EvaluateAsync<bool>("""
            () => {
              const jwt = /eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/;
              const cookie = /PoleRealtime\.Dev|__Secure-PoleRealtime/;
              const values = [location.href, document.cookie];
              // Cookie Path would hide even a non-HttpOnly cookie at the UI root. Check its
              // actual path synchronously, keeping the current workspace and history state.
              const originalUrl = location.href;
              const originalState = history.state;
              try {
                history.replaceState(originalState, '', '/api/v1/realtime/session');
                values.push(document.cookie);
              } finally {
                history.replaceState(originalState, '', originalUrl);
              }
              for (const storage of [localStorage, sessionStorage]) {
                for (let index = 0; index < storage.length; index++) {
                  const key = storage.key(index);
                  values.push(key, storage.getItem(key) ?? '');
                }
              }
              return values.every(value => !jwt.test(value) && !cookie.test(value));
            }
            """);
        Assert.True(safe && journal.UrlsSafe,
            "JWTs and realtime cookies must be absent from JavaScript-readable storage, cookies and request URLs.");
    }

    private sealed record OwnedService(
        string Id,
        string GroupId,
        string TargetId,
        string Name);

    private sealed record BootstrapMetadata(
        long Timestamp,
        DateTimeOffset ExpiresAtUtc);

    private sealed record CoverageMetadata(
        long Timestamp,
        long OpenedAt,
        int Connection,
        string Topic,
        string? ResourceId,
        string? TenantId);

    private sealed record ReadMetadata(
        long Timestamp,
        string Path);

    private sealed record ChangeMetadata(
        long Timestamp,
        string ResourceId,
        long Revision);

    private sealed class BrowserJournal
    {
        private readonly ConcurrentQueue<Task> pending = new();
        private readonly ConcurrentQueue<CoverageMetadata> coverage = new();
        private readonly ConcurrentQueue<ReadMetadata> reads = new();
        private readonly ConcurrentDictionary<IRequest, ReadMetadata> activeReads = new();
        private readonly ConcurrentQueue<ChangeMetadata> changes = new();
        private readonly ConcurrentDictionary<int, bool> closed = new();
        private static readonly Regex Jwt = new(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.CultureInvariant);
        private int connections;
        private int refreshResponses;
        private int parseFailures;
        private int unsafeUrls;
        private int unsafeHeaders;
        private int metadataReadFailures;
        private int connectionClosures;
        private readonly string tenantId;
        private long lastRefreshTimestamp;
        private long documentStartedAt;
        public ConcurrentQueue<BootstrapMetadata> Bootstraps { get; } = new();
        public int RefreshResponses => Volatile.Read(ref refreshResponses);
        public long LastRefreshTimestamp => Interlocked.Read(ref lastRefreshTimestamp);
        public int ParseFailures => Volatile.Read(ref parseFailures);
        public bool UrlsSafe => Volatile.Read(ref unsafeUrls) == 0;
        public bool HandshakeHeadersSafe => Volatile.Read(ref unsafeHeaders) == 0;
        public int MetadataReadFailures => Volatile.Read(ref metadataReadFailures);
        public int ConnectionClosures => Volatile.Read(ref connectionClosures);

        public BrowserJournal(IPage page, string tenantId)
        {
            this.tenantId = tenantId;
            page.Request += (_, request) =>
            {
                if (request.IsNavigationRequest && request.Frame == page.MainFrame)
                {
                    Interlocked.Exchange(ref documentStartedAt, Stopwatch.GetTimestamp());
                    activeReads.Clear();
                }
                CheckUrl(request.Url);
                var path = new Uri(request.Url).AbsolutePath;
                if (request.Method == "GET" && IsPilotPath(path))
                {
                    var read = new ReadMetadata(Stopwatch.GetTimestamp(), path);
                    reads.Enqueue(read);
                    activeReads[request] = read;
                }
                if ((path == "/api/v1/realtime/session" || path.StartsWith("/api/v1/realtime/hub", StringComparison.Ordinal)) &&
                    (request.Headers.ContainsKey("x-pole-tenant-id") ||
                     path == "/api/v1/realtime/session" && request.Method == "POST" && !request.Headers.ContainsKey("authorization") ||
                     path.StartsWith("/api/v1/realtime/hub", StringComparison.Ordinal) && request.Headers.ContainsKey("authorization")))
                    Interlocked.Increment(ref unsafeHeaders);
            };
            page.RequestFinished += (_, request) => FinishRead(request);
            page.RequestFailed += (_, request) => FinishRead(request);
            page.Response += (_, response) =>
            {
                var path = new Uri(response.Url).AbsolutePath;
                if (response.Request.Method == "GET" && IsPilotPath(path) && !response.Ok)
                    Interlocked.Increment(ref metadataReadFailures);
                if (response.Ok && path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal) &&
                    response.Request.Method == "POST" &&
                    response.Request.PostData?.Contains("grant_type=refresh_token", StringComparison.Ordinal) == true)
                {
                    Interlocked.Exchange(ref lastRefreshTimestamp, Stopwatch.GetTimestamp());
                    Interlocked.Increment(ref refreshResponses);
                }
                if (response.Ok && path == "/api/v1/realtime/session" && response.Request.Method == "POST")
                    pending.Enqueue(RecordBootstrapAsync(response, Stopwatch.GetTimestamp()));
            };
            page.WebSocket += (_, socket) =>
            {
                CheckUrl(socket.Url);
                if (new Uri(socket.Url).AbsolutePath != "/api/v1/realtime/hub") return;
                var connection = Interlocked.Increment(ref connections);
                var openedAt = Stopwatch.GetTimestamp();
                socket.Close += (_, _) => { closed.TryAdd(connection, true); Interlocked.Increment(ref connectionClosures); };
                socket.FrameReceived += (_, frame) => RecordCoverage(frame.Text, connection, openedAt);
            };
        }

        private static bool IsPilotPath(string path) => path == "/api/v1/web-services" ||
            path.StartsWith("/api/v1/web-services/", StringComparison.Ordinal) &&
            (!path["/api/v1/web-services/".Length..].Contains('/') || path.EndsWith("/releases", StringComparison.Ordinal));

        private void FinishRead(IRequest request)
        {
            activeReads.TryRemove(request, out _);
        }

        public int CountPilotReads(string detailPath, long started, long ended) => reads.Count(value =>
            value.Timestamp >= started && value.Timestamp <= ended &&
            (value.Path == "/api/v1/web-services" || value.Path == detailPath || value.Path == $"{detailPath}/releases"));

        public bool PilotReadsQuiet(string detailPath, TimeSpan quiet)
        {
            var latest = reads.Where(value => value.Path == "/api/v1/web-services" || value.Path == detailPath || value.Path == $"{detailPath}/releases")
                .Select(value => value.Timestamp).DefaultIfEmpty().Max();
            return !activeReads.Values.Any(value => value.Path == "/api/v1/web-services" || value.Path == detailPath || value.Path == $"{detailPath}/releases") &&
                Stopwatch.GetElapsedTime(latest) >= quiet;
        }

        public string DescribePilotReads(string detailPath) =>
            $"Pilot reads: inventory={reads.Count(value => value.Path == "/api/v1/web-services")}; detail={reads.Count(value => value.Path == detailPath)}; releases={reads.Count(value => value.Path == $"{detailPath}/releases")}; pending inventory={activeReads.Values.Count(value => value.Path == "/api/v1/web-services")}; pending detail={activeReads.Values.Count(value => value.Path == detailPath)}; bootstraps={Bootstraps.Count}; closes={ConnectionClosures}; refresh grants={RefreshResponses}.";

        public bool IsQuiet(string resourceId, TimeSpan quiet) => Stopwatch.GetElapsedTime(changes
            .Where(value => value.ResourceId == resourceId).Select(value => value.Timestamp).DefaultIfEmpty().Max()) >= quiet;
        public long LastRevision(string resourceId) => changes.Where(value => value.ResourceId == resourceId)
            .Select(value => value.Revision).DefaultIfEmpty().Max();

        private void CheckUrl(string url)
        {
            var queryKeys = new Uri(url).Query.TrimStart('?').Split('&')
                .Select(part => Uri.UnescapeDataString(part.Split('=', 2)[0]));
            if (Jwt.IsMatch(Uri.UnescapeDataString(url)) || queryKeys.Any(key => key is "access_token" or "refresh_token" or "id_token" or "PoleRealtime.Dev" or "__Secure-PoleRealtime"))
                Interlocked.Increment(ref unsafeUrls);
        }

        private async Task RecordBootstrapAsync(IResponse response, long timestamp)
        {
            try
            {
                var value = await response.JsonAsync();
                if (value is { } session && session.TryGetProperty("expiresAtUtc", out var expires))
                    Bootstraps.Enqueue(new BootstrapMetadata(timestamp, expires.GetDateTimeOffset()));
                else Interlocked.Increment(ref parseFailures);
            }
            catch (Exception failure) when (failure is PlaywrightException or JsonException or InvalidOperationException)
            {
                // Fixed counters keep response/header/credential text out of diagnostics.
                Interlocked.Increment(ref parseFailures);
            }
        }

        private void RecordCoverage(string? text, int connection, long openedAt)
        {
            if (text is null) { Interlocked.Increment(ref parseFailures); return; }
            foreach (var frame in text.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var document = JsonDocument.Parse(frame);
                    var message = document.RootElement;
                    if (message.TryGetProperty("target", out var target) && target.GetString() == "Changed")
                    {
                        var hint = message.GetProperty("arguments")[0];
                        var allowed = new[] { "version", "topic", "tenantId", "resourceId", "resourceGroupId", "revision", "generation", "observationVersion" };
                        if (hint.EnumerateObject().Any(property => !allowed.Contains(property.Name)) || hint.GetProperty("version").GetInt32() != 1)
                            Interlocked.Increment(ref parseFailures);
                        if (hint.TryGetProperty("resourceId", out var resource) && resource.ValueKind == JsonValueKind.String &&
                            hint.TryGetProperty("tenantId", out var tenant) && tenant.GetString() == tenantId)
                        {
                            var revision = hint.TryGetProperty("revision", out var version) && version.ValueKind == JsonValueKind.Number ? version.GetInt64() : 0;
                            changes.Enqueue(new ChangeMetadata(Stopwatch.GetTimestamp(), resource.GetString()!, revision));
                        }
                    }
                    if (!message.TryGetProperty("type", out var type) || type.GetInt32() != 3 ||
                        !message.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array) continue;
                    foreach (var item in result.EnumerateArray())
                    {
                        if (!item.TryGetProperty("covered", out var covered) || !covered.GetBoolean()) continue;
                        coverage.Enqueue(new CoverageMetadata(Stopwatch.GetTimestamp(), openedAt, connection,
                            Text(item, "topic"),
                            item.TryGetProperty("resourceId", out var resource) && resource.ValueKind == JsonValueKind.String ? resource.GetString() : null,
                            item.TryGetProperty("tenantId", out var tenant) && tenant.ValueKind == JsonValueKind.String ? tenant.GetString() : null));
                    }
                }
                catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
                { Interlocked.Increment(ref parseFailures); }
            }
        }

        public bool HasCoverage(string resourceId) => LatestCoveredConnection(resourceId) > 0;
        public int LatestCoveredConnection(string resourceId, long after = 0) => coverage
            .Where(value => value.ResourceId == resourceId && value.TenantId == tenantId && value.Timestamp > after &&
                value.OpenedAt > Math.Max(after, Interlocked.Read(ref documentStartedAt)) && !closed.ContainsKey(value.Connection))
            .GroupBy(value => value.Connection)
            .Where(group => new[] { "web-services", "operations", "source-builds" }.All(topic => group.Any(value => value.Topic == topic)))
            .Select(group => group.Key).DefaultIfEmpty().Max();
        public Task DrainAsync() => Task.WhenAll(pending.ToArray());
    }
}
