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
using Pole.Features.Core.Multitenancy;
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
    private readonly TestTimings _timings = new(typeof(RealtimeBrowserE2ETests));

    [Fact]
    public async Task Real_Keycloak_cookie_and_notifications_are_shared_by_two_full_API_processes()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(nameof(Real_Keycloak_cookie_and_notifications_are_shared_by_two_full_API_processes)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = budget.Token;
        var resources = new TestResourceScope("realtime root replicas E2E", timings: _timings);
        Exception? primaryFailure = null;
        var phase = "created";

        void ReportPhase(string value)
        {
            phase = value;
            output.WriteLine(
                $"Realtime replicas: phase={phase}, observedAtUtc={DateTimeOffset.UtcNow:O}; {fixture.GetRealtimeReplicaLifecycleSnapshot()}");
        }

        try
        {
            resources.Defer("stop realtime API replica", fixture.StopRealtimeReplicaAsync);
            ReportPhase("initial-start");
            var replicaAddress = await fixture.StartRealtimeReplicaAsync(ct);
            ReportPhase("initial-start-verified");
            var owned = await CreateOwnedServiceAsync(resources, ct);
            await WaitForReleaseAsync(owned.Id, 1, "Succeeded", ct);
            var origin = fixture.FrontendBaseAddress.GetLeftPart(UriPartial.Authority);
            var cookies = new CookieContainer();
            var oldCookies = new CookieContainer();
            using (var bootstrap = fixture.CreateAuthenticatedRequest(HttpMethod.Post,
                       "/api/v1/realtime/session",
                       fixture.Alpha))
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
            var firstChange =
                new TaskCompletionSource<RealtimeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondChange =
                new TaskCompletionSource<RealtimeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRevoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRevoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstAfterRestart =
                new TaskCompletionSource<RealtimeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondAfterRestart =
                new TaskCompletionSource<RealtimeChange>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second.Closed += _ =>
            {
                secondClosed.TrySetResult();
                return Task.CompletedTask;
            };
            first.On<RealtimeChange>("Changed",
                change =>
                {
                    if (change.ResourceId == owned.Id && change.Revision == 2)
                        firstChange.TrySetResult(change);
                });
            second.On<RealtimeChange>("Changed",
                change =>
                {
                    if (change.ResourceId == owned.Id && change.Revision == 2)
                        secondChange.TrySetResult(change);
                });
            first.On<RealtimeChange>("Changed",
                change =>
                {
                    if (change.ResourceId == owned.Id && change.Revision == 3)
                        firstAfterRestart.TrySetResult(change);
                });
            second.On<RealtimeChange>("Changed",
                change =>
                {
                    if (change.ResourceId == owned.Id && change.Revision == 3)
                        secondAfterRestart.TrySetResult(change);
                });
            first.On("SessionRevoked", () => firstRevoked.TrySetResult());
            second.On("SessionRevoked", () => secondRevoked.TrySetResult());
            ReportPhase("connect-and-subscribe");
            await first.StartAsync(ct);
            await second.StartAsync(ct);
            var interests = new[]
                { new RealtimeSubscriptionRequest("web-services", fixture.Alpha.DefaultTenantId, owned.Id) };
            foreach (var client in new[] { first, second })
            {
                var acknowledgement =
                    Assert.Single(
                        await client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe",
                            [interests],
                            ct));
                Assert.True(acknowledgement.Covered);
                Assert.Equal(acknowledgement.SubscriptionId,
                    Assert.Single(
                            await client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Heartbeat", [], ct))
                        .SubscriptionId);
                Assert.True(await client.InvokeCoreAsync<bool>("ReportClientLifecycle",
                    [new { reconnects = 1, fallbacks = 1 }],
                    ct));
                Assert.False(await client.InvokeCoreAsync<bool>("ReportClientLifecycle",
                    [new { reconnects = 1, fallbacks = 1 }],
                    ct));
                await Assert.ThrowsAsync<HubException>(() =>
                    client.InvokeCoreAsync<bool>("ReportClientLifecycle",
                        [new { reconnects = 101, fallbacks = 0 }],
                        ct));
                var unchanged =
                    Assert.Single(
                        await client.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Heartbeat", [], ct));
                Assert.True(unchanged.Covered);
                Assert.Equal(acknowledgement.SubscriptionId, unchanged.SubscriptionId);
            }

            await Assert.ThrowsAsync<HubException>(() => second.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>(
                "Subscribe",
                [new[] { new RealtimeSubscriptionRequest("web-services", fixture.Beta.DefaultTenantId) }],
                ct));
            ReportPhase("cross-replica-revision-2");
            Assert.Equal(2, await UpdateMetadataAsync(owned.Id, 1, "replica", ct, replicaAddress));
            await Task.WhenAll(firstChange.Task, secondChange.Task).WaitAsync(TimeSpan.FromSeconds(15), ct);
            var current = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{owned.Id}", null, ct);
            Assert.Equal(2, current.GetProperty("activeRevision").GetInt64());
            await WaitForReleaseAsync(owned.Id, 2, "Succeeded", ct);
            // Stop only this fixture's owned replica. The existing primary connection and
            // shared cookie remain alive; restart must decrypt the same ticket without bootstrap.
            ReportPhase("first-owned-stop");
            await fixture.StopRealtimeReplicaAsync(ct);
            ReportPhase("first-owned-stop-verified");
            await secondClosed.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            Assert.Equal(HubConnectionState.Connected, first.State);
            Assert.Equal(HubConnectionState.Disconnected, second.State);
            ReportPhase("restart");
            Assert.Equal(replicaAddress, await fixture.StartRealtimeReplicaAsync(ct));
            ReportPhase("restart-verified");
            await second.StartAsync(ct);
            var restarted =
                Assert.Single(
                    await second.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Subscribe", [interests], ct));
            Assert.True(restarted.Covered);
            Assert.True(await second.InvokeCoreAsync<bool>("ReportClientLifecycle",
                [new { reconnects = 1, fallbacks = 0 }],
                ct));
            Assert.Equal(restarted.SubscriptionId,
                Assert.Single(await second.InvokeCoreAsync<RealtimeSubscriptionAcknowledgement[]>("Heartbeat", [], ct))
                    .SubscriptionId);
            ReportPhase("cross-replica-revision-3");
            Assert.Equal(3, await UpdateMetadataAsync(owned.Id, 2, "replica-restarted", ct));
            await Task.WhenAll(firstAfterRestart.Task, secondAfterRestart.Task).WaitAsync(TimeSpan.FromSeconds(15), ct);
            var afterRestart = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{owned.Id}", null, ct);
            Assert.Equal(3, afterRestart.GetProperty("activeRevision").GetInt64());
            await WaitForReleaseAsync(owned.Id, 3, "Succeeded", ct);
            ReportPhase("cross-replica-logout");
            using var transport = new HttpClient(new HttpClientHandler { CookieContainer = cookies });
            transport.DefaultRequestHeaders.Add("Origin", origin);
            using var deleted = await transport.DeleteAsync(new Uri(replicaAddress, "/api/v1/realtime/session"), ct);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            // B initiates logout: notification of A's connection necessarily crosses the backplane.
            await Task.WhenAll(firstRevoked.Task, secondRevoked.Task).WaitAsync(TimeSpan.FromSeconds(15), ct);
            foreach (var client in new[] { first, second })
                await Assert.ThrowsAsync<HubException>(() =>
                    client.InvokeCoreAsync<bool>("ReportClientLifecycle", [new { reconnects = 0, fallbacks = 1 }], ct));
            using var savedTransport = new HttpClient(new HttpClientHandler { CookieContainer = oldCookies });
            savedTransport.DefaultRequestHeaders.Add("Origin", origin);
            using var rejected = await savedTransport.PostAsync(new Uri(fixture.ApiBaseAddress,
                    "/api/v1/realtime/hub/negotiate?negotiateVersion=1"),
                null,
                ct);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            ReportPhase("product-assertions-complete");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            ReportPhase($"failed-{phase}");
            throw;
        }
        finally
        {
            ReportPhase("owned-cleanup");
            try
            {
                await resources.DisposeAsync();
            }
            catch (Exception cleanupFailure) when (primaryFailure is not null)
            {
                throw new AggregateException("Two-replica realtime scenario and owned cleanup failed.",
                    primaryFailure,
                    cleanupFailure);
            }
        }
    }

    private static HubConnection ReplicaClient(Uri address, CookieContainer cookies, string origin) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(address, "/api/v1/realtime/hub"),
                options =>
                {
                    options.Cookies = cookies;
                    options.Headers["Origin"] = origin;
                    options.Transports = HttpTransportType.WebSockets;
                }).Build();

    [Fact]
    public async Task Ui_token_refresh_renews_cookie_transport_and_preserves_dirty_tenant_workspace()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(nameof(Ui_token_refresh_renews_cookie_transport_and_preserves_dirty_tenant_workspace)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = budget.Token;
        var resources = new TestResourceScope("realtime browser refresh E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            var owned = await CreateOwnedServiceAsync(resources, ct);
            await fixture.WaitForNamespaceCreationAsync(owned.GroupId, ct);
            await fixture.WaitForWebServiceReadyAsync(owned.GroupId, owned.Name, ct);
            await fixture.UseInteractiveClientTokenLifespanAsync(resources, TimeSpan.FromSeconds(40), ct);

            var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
            await using var browserContainerDisposal = _timings.MeasureDisposal(browserContainer, "e2e.browser.container.dispose");
            await browserContainer.StartAsync(ct);
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint,
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                IgnoreHTTPSErrors = true
            });
            await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
            var page = await BrowserEvidence.NewPageAsync(context);
            var journal = new BrowserJournal(page, fixture.Alpha.DefaultTenantId);
            await LoginAsync(page);
            await WaitForInitialBootstrapAsync(journal, ct);
            await page.GotoAsync(ResourceUrl(owned.Id, "configuration"));
            await Assertions.Expect(page.GetByRole(AriaRole.Heading,
                new PageGetByRoleOptions { Name = "Service configuration", Exact = true })).ToBeVisibleAsync();
            await WaitUntilAsync(() => journal.Bootstraps.Count > 0 && journal.HasCoverage(owned.Id),
                TimeSpan.FromSeconds(30),
                "The real browser did not acknowledge pilot coverage.",
                ct);
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
                TimeSpan.FromSeconds(75),
                "The interactive client did not complete a real refresh_token grant.",
                ct);
            var refreshCompleted = journal.LastRefreshTimestamp;
            await WaitUntilAsync(() =>
                {
                    var bootstrap = journal.Bootstraps.LastOrDefault(value => value.Timestamp > refreshCompleted &&
                                                                              value.ExpiresAtUtc >
                                                                              originalBootstrap.ExpiresAtUtc);
                    return bootstrap is not null &&
                           journal.LatestCoveredConnection(owned.Id, bootstrap.Timestamp) > originalConnection;
                },
                TimeSpan.FromSeconds(30),
                "Refreshed credentials did not bootstrap and acknowledge a new cookie connection.",
                ct);

            Assert.Equal(fixture.Alpha.DefaultTenantId, await page.GetByLabel("Tenant").InputValueAsync());
            Assert.Equal(workspaceTabCount, await workspaceTabs.GetByRole(AriaRole.Tab).CountAsync());
            await Assertions.Expect(selectedResource).ToHaveAttributeAsync("aria-selected", "true");
            await Assertions.Expect(page.GetByRole(AriaRole.Tab,
                    new PageGetByRoleOptions { Name = "Configuration", Exact = true }))
                .ToHaveAttributeAsync("aria-selected", "true");
            Assert.Equal(draft, await page.GetByLabel("Environment variables").InputValueAsync());
            await AssertNoReadableCredentialsAsync(page, journal);
            Assert.True(journal.HandshakeHeadersSafe,
                "Realtime bootstrap and hub requests must not contain a tenant header or hub bearer authentication.");
            await journal.DrainAsync();
            Assert.True(journal.ParseFailures == 0, journal.DescribeParseFailures());
        });
    }

    [Fact]
    public async Task Owned_realtime_Redis_outage_falls_back_and_recovers_without_remounting_dirty_workspace()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(nameof(Owned_realtime_Redis_outage_falls_back_and_recovers_without_remounting_dirty_workspace)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = budget.Token;
        var resources = new TestResourceScope("realtime owned Redis outage E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            var owned = await CreateOwnedServiceAsync(resources, ct);
            await fixture.WaitForNamespaceCreationAsync(owned.GroupId, ct);
            await fixture.WaitForWebServiceReadyAsync(owned.GroupId, owned.Name, ct);
            await fixture.UseInteractiveClientTokenLifespanAsync(resources, TimeSpan.FromMinutes(5), ct);
            var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
            await using var browserContainerDisposal = _timings.MeasureDisposal(browserContainer, "e2e.browser.container.dispose");
            await browserContainer.StartAsync(ct);
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint,
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                IgnoreHTTPSErrors = true
            });
            await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
            var page = await BrowserEvidence.NewPageAsync(context);
            var journal = new BrowserJournal(page, fixture.Alpha.DefaultTenantId);
            await LoginAsync(page);
            await WaitForInitialBootstrapAsync(journal, ct);
            await page.GotoAsync(ResourceUrl(owned.Id, "configuration"));
            await Assertions.Expect(page.GetByRole(AriaRole.Heading,
                new PageGetByRoleOptions { Name = "Service configuration", Exact = true })).ToBeVisibleAsync();
            await WaitUntilAsync(() => journal.HasCoverage(owned.Id),
                TimeSpan.FromSeconds(30),
                "The outage scenario did not establish acknowledged browser coverage.",
                ct);
            var originalConnection = journal.LatestCoveredConnection(owned.Id);
            var navigationCount = journal.DocumentNavigations;
            const string draft = "REALTIME_REDIS_OUTAGE_DRAFT=unsaved";
            var environment = page.GetByLabel("Environment variables");
            await environment.FillAsync(draft);
            var originalEditor = await environment.ElementHandleAsync()
                                 ?? throw new InvalidOperationException("The dirty configuration editor is unavailable.");
            var workspaceTabs = page.GetByRole(AriaRole.Tablist,
                new PageGetByRoleOptions { Name = "Web service workspace", Exact = true });
            var tabCount = await workspaceTabs.GetByRole(AriaRole.Tab).CountAsync();
            var selectedResource = workspaceTabs.GetByRole(AriaRole.Tab,
                new LocatorGetByRoleOptions { Name = owned.Name, Exact = false });
            var sections = page.GetByRole(AriaRole.Tablist,
                new PageGetByRoleOptions { Name = $"{owned.Name} sections", Exact = true });
            var detailPath = $"/api/v1/web-services/{owned.Id}";
            // This cleanup precedes the only outage action and executes before resource teardown.
            // The command targets the isolated Aspire graph, never the reusable Kubernetes lab.
            resources.Defer("restore owned realtime Redis", fixture.StartRealtimeRedisAsync);
            await fixture.StopRealtimeRedisAsync(ct);
            await WaitUntilAsync(() => !journal.HasCoverage(owned.Id),
                TimeSpan.FromSeconds(30),
                "The browser did not release real coverage after its owned Redis became unavailable.",
                ct);
            Assert.Equal(2, await UpdateMetadataAsync(owned.Id, 1, "redis-outage", ct));
            await sections.GetByRole(AriaRole.Tab,
                new LocatorGetByRoleOptions { Name = "Overview", Exact = true }).ClickAsync();
            var visibleRevision = page.GetByText("Active revision", new PageGetByTextOptions { Exact = true })
                .Locator("xpath=following-sibling::dd");
            // Stable details retain their original manual fallback; only reconciling
            // deployments use the existing 2.5s loop (43e5cab3 interval=false).
            var refreshedAt = Stopwatch.GetTimestamp();
            await page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Refresh service details", Exact = true }).ClickAsync();
            await WaitUntilAsync(() => journal.HasDetailRead(detailPath, refreshedAt),
                TimeSpan.FromSeconds(15),
                "The uncovered browser did not perform its requested manual detail GET.",
                ct);
            await Assertions.Expect(visibleRevision)
                .ToHaveTextAsync("2", new LocatorAssertionsToHaveTextOptions { Timeout = 15000 });
            Assert.False(journal.HasCoverage(owned.Id));
            Assert.True(await originalEditor.EvaluateAsync<bool>("element => element.isConnected"),
                "Redis fallback must keep the dirty configuration form mounted.");
            var restoredAt = Stopwatch.GetTimestamp();
            await fixture.StartRealtimeRedisAsync(ct);
            await WaitUntilAsync(() => journal.Bootstraps.Any(value => value.Timestamp > restoredAt) &&
                                       journal.LatestCoveredConnection(owned.Id, restoredAt) > originalConnection,
                TimeSpan.FromSeconds(60),
                "Redis recovery did not bootstrap and acknowledge a new browser connection.",
                ct);
            var bootstrap = journal.Bootstraps.Last(value => value.Timestamp > restoredAt);
            await WaitUntilAsync(() => journal.HasDetailRead(detailPath, bootstrap.Timestamp),
                TimeSpan.FromSeconds(15),
                "Acknowledged recovery did not resync authoritative detail metadata.",
                ct);
            Assert.Equal(3, await UpdateMetadataAsync(owned.Id, 2, "redis-recovered", ct));
            await WaitUntilAsync(() => journal.LastRevision(owned.Id) >= 3,
                TimeSpan.FromSeconds(20),
                "Recovered Redis did not deliver a committed metadata hint.",
                ct);
            await Assertions.Expect(visibleRevision)
                .ToHaveTextAsync("3", new LocatorAssertionsToHaveTextOptions { Timeout = 20000 });
            await sections.GetByRole(AriaRole.Tab,
                new LocatorGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
            Assert.True(
                await environment.EvaluateAsync<bool>("(element, original) => element === original", originalEditor),
                "Outage recovery must retain the original dirty editor DOM node.");
            Assert.Equal(draft, await environment.InputValueAsync());
            Assert.Equal(fixture.Alpha.DefaultTenantId, await page.GetByLabel("Tenant").InputValueAsync());
            Assert.Equal(tabCount, await workspaceTabs.GetByRole(AriaRole.Tab).CountAsync());
            await Assertions.Expect(selectedResource).ToHaveAttributeAsync("aria-selected", "true");
            Assert.Equal(navigationCount, journal.DocumentNavigations);
            Assert.Equal(0, journal.MetadataReadFailures);
            await AssertNoReadableCredentialsAsync(page, journal);
            Assert.True(journal.HandshakeHeadersSafe);
            await journal.DrainAsync();
            Assert.True(journal.ParseFailures == 0, journal.DescribeParseFailures());
        });
    }

    [Fact]
    public async Task Same_account_tabs_keep_different_tenant_notifications_and_dirty_workspaces_isolated()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(nameof(Same_account_tabs_keep_different_tenant_notifications_and_dirty_workspaces_isolated)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = budget.Token;
        var resources = new TestResourceScope("realtime same-account tenant tabs E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            var alphaTenant = fixture.Alpha.DefaultTenantId;
            var betaTenant = fixture.Beta.DefaultTenantId;
            var member = await SendAsync(HttpMethod.Post,
                $"/api/v1/tenants/{alphaTenant}/members",
                new
                {
                    userId = fixture.Beta.KeycloakUserId,
                    email = fixture.Beta.Email,
                    role = TenantRole.Member.ToString()
                },
                ct);
            var memberId = Text(member, "id");
            resources.Defer("remove owned realtime Alpha membership",
                cleanup => DeleteAsync($"/api/v1/tenants/{alphaTenant}/members/{memberId}", cleanup));
            var alpha = await CreateOwnedServiceAsync(resources, ct);
            var beta = await CreateOwnedServiceAsync(resources, ct, fixture.Beta);
            await fixture.WaitForNamespaceCreationAsync(alpha.GroupId, ct);
            await fixture.WaitForNamespaceCreationAsync(beta.GroupId, ct, fixture.Beta);
            await fixture.WaitForWebServiceReadyAsync(alpha.GroupId, alpha.Name, ct);
            await fixture.WaitForWebServiceReadyAsync(beta.GroupId, beta.Name, ct);
            await fixture.UseInteractiveClientTokenLifespanAsync(resources, TimeSpan.FromMinutes(5), ct);
            var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
            await using var browserContainerDisposal = _timings.MeasureDisposal(browserContainer, "e2e.browser.container.dispose");
            await browserContainer.StartAsync(ct);
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint,
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                IgnoreHTTPSErrors = true
            });
            await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
            var alphaPage = await BrowserEvidence.NewPageAsync(context);
            var alphaJournal = new BrowserJournal(alphaPage, alphaTenant);
            await LoginAsync(alphaPage, fixture.Beta);
            await WaitForInitialBootstrapAsync(alphaJournal, ct);
            await alphaPage.GotoAsync(ResourceUrl(alpha.Id, "configuration", alphaTenant));
            await Assertions.Expect(alphaPage.GetByLabel("Environment variables")).ToBeVisibleAsync();
            await WaitUntilAsync(() => alphaJournal.HasCoverage(alpha.Id),
                TimeSpan.FromSeconds(30),
                "The shared account did not acknowledge its Alpha resource.",
                ct);
            const string alphaDraft = "REALTIME_ALPHA_TAB_DRAFT=unsaved";
            await alphaPage.GetByLabel("Environment variables").FillAsync(alphaDraft);
            var alphaEditor = await alphaPage.GetByLabel("Environment variables").ElementHandleAsync()
                              ?? throw new InvalidOperationException("The Alpha dirty editor is unavailable.");
            alphaJournal.TrackTenantReads();
            var betaPage = await BrowserEvidence.NewPageAsync(context);
            var betaJournal = new BrowserJournal(betaPage, betaTenant);
            // Same browser context: the real Keycloak SSO and HttpOnly realtime cookie are shared.
            await betaPage.GotoAsync(ResourceUrl(beta.Id, "configuration", betaTenant));
            await Assertions.Expect(betaPage.GetByLabel("Environment variables"))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await WaitUntilAsync(() => betaJournal.HasCoverage(beta.Id),
                TimeSpan.FromSeconds(30),
                "The shared account did not acknowledge its Beta resource.",
                ct);
            const string betaDraft = "REALTIME_BETA_TAB_DRAFT=unsaved";
            await betaPage.GetByLabel("Environment variables").FillAsync(betaDraft);
            var betaEditor = await betaPage.GetByLabel("Environment variables").ElementHandleAsync()
                             ?? throw new InvalidOperationException("The Beta dirty editor is unavailable.");
            betaJournal.TrackTenantReads();
            var alphaNavigation = alphaJournal.DocumentNavigations;
            var betaNavigation = betaJournal.DocumentNavigations;
            await alphaPage.BringToFrontAsync();
            await ClickResourceSectionAsync(alphaPage, alpha.Name, "Overview");
            await WaitUntilAsync(() => alphaJournal.HasCoverage(alpha.Id),
                TimeSpan.FromSeconds(30),
                "Activating Alpha did not restore its own acknowledged coverage.",
                ct);
            Assert.Equal(2, await UpdateMetadataAsync(alpha.Id, 1, "alpha-tab", ct));
            await WaitUntilAsync(() => alphaJournal.LastRevision(alpha.Id) >= 2,
                TimeSpan.FromSeconds(20),
                "The committed Alpha change did not reach its owning tab.",
                ct);
            await Assertions.Expect(ActiveRevision(alphaPage)).ToHaveTextAsync("2");
            await betaPage.BringToFrontAsync();
            await ClickResourceSectionAsync(betaPage, beta.Name, "Overview");
            await WaitUntilAsync(() => betaJournal.HasCoverage(beta.Id),
                TimeSpan.FromSeconds(30),
                "Activating Beta did not restore its own acknowledged coverage.",
                ct);
            await Assertions.Expect(ActiveRevision(betaPage)).ToHaveTextAsync("1");
            Assert.Equal(2, await UpdateMetadataAsync(beta.Id, 1, "beta-tab-2", ct, owner: fixture.Beta));
            Assert.Equal(3, await UpdateMetadataAsync(beta.Id, 2, "beta-tab-3", ct, owner: fixture.Beta));
            await WaitUntilAsync(() => betaJournal.LastRevision(beta.Id) >= 3,
                TimeSpan.FromSeconds(20),
                "The committed Beta change did not reach its owning tab.",
                ct);
            await Assertions.Expect(ActiveRevision(betaPage)).ToHaveTextAsync("3");
            await alphaPage.BringToFrontAsync();
            await WaitUntilAsync(() => alphaJournal.HasCoverage(alpha.Id),
                TimeSpan.FromSeconds(30),
                "Returning to Alpha did not retain its independent resource coverage.",
                ct);
            await Assertions.Expect(ActiveRevision(alphaPage)).ToHaveTextAsync("2");
            foreach (var (page, journal, owned, tenantId, draft, editor, navigation) in new[]
                     {
                     (alphaPage, alphaJournal, alpha, alphaTenant, alphaDraft, alphaEditor, alphaNavigation),
                     (betaPage, betaJournal, beta, betaTenant, betaDraft, betaEditor, betaNavigation)
                 })
            {
                await page.BringToFrontAsync();
                await ClickResourceSectionAsync(page, owned.Name, "Configuration");
                Assert.Equal(tenantId, await page.GetByLabel("Tenant").InputValueAsync());
                Assert.Equal(draft, await page.GetByLabel("Environment variables").InputValueAsync());
                Assert.True(await page.GetByLabel("Environment variables")
                    .EvaluateAsync<bool>("(element, original) => element === original", editor));
                var foreign = owned == alpha ? beta : alpha;
                Assert.Equal(0,
                    await page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = foreign.Name, Exact = true })
                        .CountAsync());
                var safeWorkspace = await page.EvaluateAsync<bool>("""
                ([tenant, resource]) => {
                  const records = Object.entries(localStorage).filter(([key]) => key.endsWith('.' + tenant));
                  return records.length > 0 && records.every(([, value]) => !value.includes(resource));
                }
                """,
                    new[] { tenantId, foreign.Id });
                Assert.True(safeWorkspace, "Persisted workspace references must exclude the other tenant's resource.");
                Assert.Equal(0, journal.TenantReadScopeViolations);
                Assert.Equal(0, journal.ForeignTenantHints);
                Assert.Equal(navigation, journal.DocumentNavigations);
                await AssertNoReadableCredentialsAsync(page, journal);
                Assert.True(journal.HandshakeHeadersSafe);
                await journal.DrainAsync();
                Assert.True(journal.ParseFailures == 0, journal.DescribeParseFailures());
            }
        });
    }

    private static ILocator ActiveRevision(IPage page) => page
        .GetByText("Active revision", new PageGetByTextOptions { Exact = true })
        .Locator("xpath=following-sibling::dd");

    private static Task ClickResourceSectionAsync(IPage page, string name, string section) => page.GetByRole(
        AriaRole.Tablist,
        new PageGetByRoleOptions { Name = $"{name} sections", Exact = true }).GetByRole(AriaRole.Tab,
        new LocatorGetByRoleOptions { Name = section, Exact = true }).ClickAsync();

    [Fact]
    public async Task Covered_pending_release_reduces_metadata_reads_against_real_disconnected_polling()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(nameof(Covered_pending_release_reduces_metadata_reads_against_real_disconnected_polling)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = budget.Token;
        const int windowSeconds = 65;
        var resources = new TestResourceScope("realtime browser polling E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            var owned = await CreateOwnedServiceAsync(resources, ct);
            await fixture.WaitForNamespaceCreationAsync(owned.GroupId, ct);
            await WaitForReleaseAsync(owned.Id, 1, "Succeeded", ct);
            // The target belongs to this disposable fixture's Mongo/API graph. This follows the
            // ReleaseBrowserE2ETests pattern; the shared lab and its credentials remain untouched.
            resources.Defer("restore isolated realtime target availability",
                cleanup => SetTargetEnabledAsync(owned.TargetId, true, cleanup));
            await SetTargetEnabledAsync(owned.TargetId, false, ct);
            await SendAsync(HttpMethod.Post, $"/api/v1/web-services/{owned.Id}/redeploy?expectedRevision=1", null, ct);
            await WaitUntilAsync(async probeCancellation =>
                {
                    var value = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{owned.Id}", null, probeCancellation);
                    var placement = value.GetProperty("deployments")[0];
                    return Text(placement, "status") == "Failed" &&
                           Text(placement, "failureCode") == "DeploymentTargetDisabled";
                },
                TimeSpan.FromMinutes(2),
                "The real pending release did not reach its controlled disabled-target state.",
                ct);
            // Disabled-target failures are retryable. Use the public fenced retry command
            // to reach a backoff long enough for both windows, rather than measuring real
            // reconciliation writes as if they were idle polling.
            await PrepareQuietFailedDeploymentAsync(owned, TimeSpan.FromSeconds(windowSeconds * 2 + 60), ct);
            await fixture.UseInteractiveClientTokenLifespanAsync(resources, TimeSpan.FromMinutes(5), ct);

            var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
            await using var browserContainerDisposal = _timings.MeasureDisposal(browserContainer, "e2e.browser.container.dispose");
            await browserContainer.StartAsync(ct);
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint,
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            var baseline = await MeasurePendingReadsAsync(browser, owned, disconnected: true, windowSeconds, ct);
            var covered = await MeasurePendingReadsAsync(browser, owned, disconnected: false, windowSeconds, ct);
            Assert.True(baseline >= 20,
                "The pending-release fallback must establish an actual 2.5-second polling baseline.");
            var reduction = 1d - (double)covered / baseline;
            output.WriteLine(
                $"Controlled pending release: {windowSeconds}s per window; fallback GETs={baseline}; covered GETs={covered}; reduction={reduction:P1}.");
            output.WriteLine(
                "Ordinary 20s polling versus the 60s safety interval has a theoretical 66.7% steady-state reduction; this 80% gate is specific to the controlled 2.5s pending-release pilot.");
            Assert.True(reduction >= 0.8,
                "Acknowledged pilot coverage must reduce controlled pending-release metadata GETs by at least 80% against its measured disconnected baseline.");
            await SetTargetEnabledAsync(owned.TargetId, true, ct);
            // Quiet sampling deliberately retains a long failure backoff. Resume this same
            // owned Apply through its public retry command after restoring the target.
            await WaitForQuietReleaseRecoveryAsync(owned, resourceRevision: 2, ct);
        });
    }

    [Fact]
    public async Task Committed_metadata_bursts_keep_a_slow_read_alive_and_reach_visible_revisions_within_budget()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(nameof(Committed_metadata_bursts_keep_a_slow_read_alive_and_reach_visible_revisions_within_budget)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = budget.Token;
        var resources = new TestResourceScope("realtime browser metadata E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            var owned = await CreateOwnedServiceAsync(resources, ct);
            await fixture.WaitForNamespaceCreationAsync(owned.GroupId, ct);
            await fixture.WaitForWebServiceReadyAsync(owned.GroupId, owned.Name, ct);
            var browserContainer = new PlaywrightBrowserContainer(fixture.Identity);
            await using var browserContainerDisposal = _timings.MeasureDisposal(browserContainer, "e2e.browser.container.dispose");
            await browserContainer.StartAsync(ct);
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(browserContainer.WebSocketEndpoint,
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                IgnoreHTTPSErrors = true
            });
            await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
            var page = await BrowserEvidence.NewPageAsync(context);
            var journal = new BrowserJournal(page, fixture.Alpha.DefaultTenantId);
            await LoginAsync(page);
            await WaitForInitialBootstrapAsync(journal, ct);
            await page.GotoAsync(ResourceUrl(owned.Id, "overview"));
            var visibleRevision = page.Locator(".web-service-overview dl div")
                .Filter(new LocatorFilterOptions { HasText = "Active revision" }).Locator("dd");
            await Assertions.Expect(visibleRevision).ToHaveTextAsync("1");
            await WaitUntilAsync(() => journal.HasCoverage(owned.Id),
                TimeSpan.FromSeconds(30),
                "The metadata pilot did not acknowledge real coverage.",
                ct);
            var detailPath = $"/api/v1/web-services/{owned.Id}";
            var acknowledgedAt = Stopwatch.GetTimestamp();
            try
            {
                await WaitUntilAsync(() => Stopwatch.GetElapsedTime(acknowledgedAt) >= TimeSpan.FromMilliseconds(500) &&
                                           journal.PilotReadsQuiet(detailPath, TimeSpan.FromMilliseconds(500)),
                    TimeSpan.FromSeconds(10),
                    "Initial metadata subscriptions and their HTTP resync did not settle.",
                    ct);
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
            await page.RouteAsync(detailRoute,
                async route =>
                {
                    if (route.Request.Method != "GET")
                    {
                        await route.ContinueAsync();
                        return;
                    }

                    if (Interlocked.Increment(ref reads) != 1)
                    {
                        await route.ContinueAsync();
                        return;
                    }

                    IAPIResponse? realResponse = null;
                    try
                    {
                        realResponse = await route.FetchAsync();
                        Assert.True(realResponse.Ok, "The delayed snapshot must come from the real authenticated API.");
                        held.TrySetResult();
                        await release.Task;
                        await route.FulfillAsync(new RouteFulfillOptions { Response = realResponse });
                        delivered.TrySetResult();
                    }
                    catch (Exception)
                    {
                        var failure = new InvalidOperationException("The delayed metadata interception failed.");
                        held.TrySetException(failure);
                        delivered.TrySetException(failure);
                    }
                    finally
                    {
                        if (realResponse is not null)
                            await realResponse.DisposeAsync();
                    }
                });
            long revision = 1;
            try
            {
                revision = await UpdateMetadataAsync(owned.Id, revision, "burst-0", ct);
                await held.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                for (var index = 1; index < 8; index++)
                    revision = await UpdateMetadataAsync(owned.Id, revision, $"burst-{index}", ct);
                await WaitForReleaseAsync(owned.Id, revision, "Succeeded", ct);
                await WaitUntilAsync(() =>
                        journal.LastRevision(owned.Id) >= revision && journal.IsQuiet(owned.Id, TimeSpan.FromSeconds(2)),
                    TimeSpan.FromSeconds(20),
                    "The committed metadata burst did not reach the real browser before releasing its old GET.",
                    ct);
                Assert.Equal(1, Volatile.Read(ref reads));
                Assert.Equal(0, Volatile.Read(ref canceled));
                release.TrySetResult();
                await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                await Assertions.Expect(visibleRevision).ToHaveTextAsync(
                    revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new LocatorAssertionsToHaveTextOptions { Timeout = 10_000 });
                await WaitUntilAsync(() => journal.IsQuiet(owned.Id, TimeSpan.FromSeconds(1)),
                    TimeSpan.FromSeconds(5),
                    "The trailing metadata read did not settle.",
                    ct);
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
                await Assertions.Expect(visibleRevision).ToHaveTextAsync(
                    revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new LocatorAssertionsToHaveTextOptions { Timeout = 10_000 });
                samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            samples.Sort();
            var p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
            output.WriteLine(
                $"Committed WS metadata: {samples.Count} write-start-to-visible-revision samples; p95={p95:F0}ms (includes write request and commit).");
            Assert.True(p95 <= 3000,
                "The sampled metadata commit-to-visible-revision p95 upper bound must be at most three seconds.");
            await AssertNoReadableCredentialsAsync(page, journal);
            await journal.DrainAsync();
            Assert.True(journal.ParseFailures == 0, journal.DescribeParseFailures());
        });
    }

    private async Task<int> MeasurePendingReadsAsync(IBrowser browser,
        OwnedService owned,
        bool disconnected,
        int windowSeconds,
        CancellationToken ct)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            IgnoreHTTPSErrors = true
        });
        await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
        var page = await BrowserEvidence.NewPageAsync(context);
        if (disconnected)
            await page.RouteAsync("**/api/v1/realtime/**", route => route.AbortAsync("failed"));
        var journal = new BrowserJournal(page, fixture.Alpha.DefaultTenantId);
        await LoginAsync(page);
        if (!disconnected)
            await WaitForInitialBootstrapAsync(journal, ct);
        await page.GotoAsync(ResourceUrl(owned.Id, "releases"));
        var pendingRelease = page.Locator("[aria-label='Release revision 2']").GetByTestId("release-status");
        await Assertions.Expect(pendingRelease).ToHaveTextAsync("Pending");
        if (!disconnected)
            await WaitUntilAsync(() => journal.HasCoverage(owned.Id),
                TimeSpan.FromSeconds(30),
                "The covered pending-release sample did not acknowledge subscriptions.",
                ct);
        // Wait for initial reads and the subscribe acknowledgement's resync outside the window.
        var detailPath = $"/api/v1/web-services/{owned.Id}";
        var acknowledgedAt = Stopwatch.GetTimestamp();
        try
        {
            await WaitUntilAsync(() => Stopwatch.GetElapsedTime(acknowledgedAt) >= TimeSpan.FromMilliseconds(500) &&
                                       journal.PilotReadsQuiet(detailPath, TimeSpan.FromMilliseconds(500)) &&
                                       (disconnected || journal.IsQuiet(owned.Id, TimeSpan.FromSeconds(3))),
                TimeSpan.FromSeconds(10),
                "Initial pending-release metadata reads did not settle.",
                ct);
        }
        catch (TimeoutException)
        {
            output.WriteLine(journal.DescribePilotReads(detailPath));
            throw;
        }
        var before = await LoadQuietOperationAsync(owned.Id, ct);
        Assert.Equal("Failed", Text(before, "phase"));
        Assert.True(before.GetProperty("retryNotBeforeUtc").GetDateTime().ToUniversalTime() >
                    DateTime.UtcNow.AddSeconds(windowSeconds + 5),
            "The metadata window must finish before automatic reconciliation is due.");
        var started = Stopwatch.GetTimestamp();
        var connectionClosures = journal.ConnectionClosures;
        var end = started + windowSeconds * Stopwatch.Frequency;
        using var windowTiming = _timings.Measure(disconnected
            ? "e2e.realtime.window.disconnected" : "e2e.realtime.window.covered");
        await WaitUntilAsync(() => Stopwatch.GetTimestamp() >= end,
            TimeSpan.FromSeconds(windowSeconds + 5),
            "The metadata sampling window did not finish.",
            ct);
        var finished = Stopwatch.GetTimestamp();
        windowTiming.Dispose();
        var after = await LoadQuietOperationAsync(owned.Id, ct);
        Assert.Equal("Failed", Text(after, "phase"));
        Assert.Equal(before.GetProperty("attemptCount").GetInt32(), after.GetProperty("attemptCount").GetInt32());
        await Assertions.Expect(pendingRelease).ToHaveTextAsync("Pending");
        if (disconnected)
            Assert.False(journal.HasCoverage(owned.Id));
        else
        {
            Assert.True(journal.HasCoverage(owned.Id),
                "Realtime coverage must remain confirmed throughout the sample.");
            output.WriteLine(
                $"Covered window: bootstraps={journal.Bootstraps.Count}; closures before={connectionClosures}; closures after={journal.ConnectionClosures}; refresh grants={journal.RefreshResponses}.");
            Assert.Equal(connectionClosures, journal.ConnectionClosures);
            Assert.Equal(0, journal.CountChanges(owned.Id, started, finished));
        }

        var count = journal.CountPilotReads(detailPath, started, finished);
        Assert.Equal(0, journal.MetadataReadFailures);
        await AssertNoReadableCredentialsAsync(page, journal);
        await journal.DrainAsync();
        Assert.True(journal.ParseFailures == 0, journal.DescribeParseFailures());
        return count;
    }

    private async Task<JsonElement> LoadQuietOperationAsync(string resourceId, CancellationToken ct)
    {
        var page = await SendAsync(HttpMethod.Get,
            $"/api/v1/resource-deployments/operations?resourceType=pole%2Fweb-service&resourceId={resourceId}",
            null,
            ct);
        return page.GetProperty("items").EnumerateArray().Single().Clone();
    }

    private async Task PrepareQuietFailedDeploymentAsync(OwnedService owned, TimeSpan horizon, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            JsonElement operation = default;
            await WaitUntilAsync(async probeCancellation =>
                {
                    operation = await LoadQuietOperationAsync(owned.Id, probeCancellation);
                    return Text(operation, "phase") == "Failed" && operation.GetProperty("retryable").GetBoolean();
                },
                TimeSpan.FromMinutes(1),
                "The owned disabled-target deployment did not reach its retryable failure.",
                ct);
            var deployment = operation.GetProperty("deployment");
            var placement = deployment.GetProperty("placement");
            Assert.Equal("DeploymentTargetDisabled", Text(placement, "failureCode"));
            if (operation.GetProperty("retryNotBeforeUtc").GetDateTime().ToUniversalTime() > DateTime.UtcNow + horizon)
                return;
            var count = operation.GetProperty("attemptCount").GetInt32();
            using var retry = fixture.CreateAuthenticatedRequest(HttpMethod.Post,
                $"/api/v1/resource-deployments/{Text(deployment, "id")}/retry?expectedRevision={placement.GetProperty("deploymentRevision").GetInt64()}&expectedOperation=Apply",
                fixture.Alpha,
                fixture.Alpha.DefaultTenantId);
            using var response = await fixture.Api.SendAsync(retry, ct);
            if (response.StatusCode == HttpStatusCode.Conflict)
                continue; // A real recovery race requires a fresh state.
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            await WaitUntilAsync(async probeCancellation =>
                {
                    var current = await LoadQuietOperationAsync(owned.Id, probeCancellation);
                    return Text(current, "phase") == "Failed" && current.GetProperty("attemptCount").GetInt32() > count;
                },
                TimeSpan.FromMinutes(1),
                "The owned fenced retry did not complete its disabled-target attempt.",
                ct);
        }

        throw new InvalidOperationException(
            "The controlled pending release did not reach a backoff covering both quiet metadata windows.");
    }

    private async Task WaitForQuietReleaseRecoveryAsync(OwnedService owned, long resourceRevision, CancellationToken ct)
    {
        string? expectedDeploymentId = null;
        string? lastState = null;
        await WaitUntilAsync(async probeCancellation =>
            {
                var operation = await LoadQuietOperationAsync(owned.Id, probeCancellation);
                var deployment = operation.GetProperty("deployment");
                var placement = deployment.GetProperty("placement");
                Assert.Equal("pole/web-service", Text(deployment, "resourceType"));
                Assert.Equal(owned.Id, Text(deployment, "resourceId"));
                Assert.Equal(owned.TargetId, Text(placement, "deploymentTargetId"));
                Assert.Equal(resourceRevision, placement.GetProperty("resourceRevision").GetInt64());
                Assert.Equal("Apply", Text(placement, "desiredOperation"));
                Assert.Equal(placement.GetProperty("desiredGeneration").GetInt64(),
                    operation.GetProperty("operationGeneration").GetInt64());
                var deploymentId = Text(deployment, "id");
                expectedDeploymentId ??= deploymentId;
                Assert.Equal(expectedDeploymentId, deploymentId);
                var phase = Text(operation, "phase");
                Assert.True(phase is "Failed" or "Accepted" or "Running" or "Applied",
                    "The restored owned Apply entered an unexpected operation phase.");
                if (phase == "Failed")
                {
                    Assert.True(operation.GetProperty("retryable").GetBoolean(),
                        "The restored owned Apply failure must be retryable.");
                    Assert.Equal("DeploymentTargetDisabled", Text(placement, "failureCode"));
                }

                var retryValue = operation.GetProperty("retryNotBeforeUtc");
                var retryAt = retryValue.ValueKind == JsonValueKind.Null
                    ? (DateTime?)null
                    : retryValue.GetDateTime().ToUniversalTime();
                var attempts = operation.GetProperty("attemptCount").GetInt32();
                var revision = placement.GetProperty("deploymentRevision").GetInt64();
                var state = $"{phase}/{attempts}/{revision}/{retryAt:O}";
                if (state != lastState)
                {
                    var remaining = retryAt is { } due ? Math.Max(0, (due - DateTime.UtcNow).TotalSeconds) : 0;
                    output.WriteLine(
                        $"Quiet recovery: phase={phase}; attemptCount={attempts}; retryAtUtc={retryAt?.ToString("O") ?? "none"}; remainingBackoffSeconds={remaining:F1}.");
                    lastState = state;
                }

                var history = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{owned.Id}/releases", null, probeCancellation);
                Assert.Equal(resourceRevision, history.GetProperty("activeRevision").GetInt64());
                if (history.GetProperty("releases").GetProperty("items").EnumerateArray().Any(value =>
                        value.GetProperty("resourceRevision").GetInt64() == resourceRevision &&
                        Text(value, "outcome") == "Succeeded"))
                    return true;
                if (phase != "Failed")
                    return false; // An accepted/running/applied operation must complete without another write.
                // Capture the immutable public command from this current owned snapshot. A
                // recovery race returns 409 and requires a new GET rather than reusing it.
                using var retry = fixture.CreateAuthenticatedRequest(HttpMethod.Post,
                    $"/api/v1/resource-deployments/{deploymentId}/retry?expectedRevision={revision}&expectedOperation=Apply",
                    fixture.Alpha,
                    fixture.Alpha.DefaultTenantId);
                using var response = await fixture.Api.SendAsync(retry, probeCancellation);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    output.WriteLine("Quiet recovery: retry=Conflict; reloading current owned operation.");
                    return false;
                }

                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                output.WriteLine("Quiet recovery: retry=Accepted.");
                return false;
            },
            TimeSpan.FromMinutes(4),
            "The restored owned realtime release did not reach its required outcome.",
            ct);
    }

    private async Task<long> UpdateMetadataAsync(string id,
        long expectedRevision,
        string marker,
        CancellationToken ct,
        Uri? apiAddress = null,
        TestUser? owner = null)
    {
        var path = $"/api/v1/web-services/{id}";
        output.WriteLine($"Metadata update: expectedRevision={expectedRevision}, step={marker}.");
        var accepted = await SendAsync(HttpMethod.Put,
            apiAddress is null ? path : new Uri(apiAddress, path).ToString(),
            new
            {
                spec = Spec("initial"),
                tags = new[] { "e2e", marker },
                exposure = "Private",
                expectedRevision
            },
            ct,
            owner);
        return accepted.GetProperty("resourceRevision").GetInt64();
    }

    private async Task WaitForReleaseAsync(string id, long revision, string outcome, CancellationToken ct)
    {
        await WaitUntilAsync(async probeCancellation =>
            {
                var history = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{id}/releases", null, probeCancellation);
                return history.GetProperty("releases").GetProperty("items").EnumerateArray().Any(value =>
                    value.GetProperty("resourceRevision").GetInt64() == revision && Text(value, "outcome") == outcome);
            },
            TimeSpan.FromMinutes(4),
            "The owned realtime release did not reach its required outcome.",
            ct);
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

    private async Task<OwnedService> CreateOwnedServiceAsync(TestResourceScope resources,
        CancellationToken ct,
        TestUser? owner = null)
    {
        owner ??= fixture.Alpha;
        var name = $"realtime-{Guid.NewGuid():N}"[..20];
        var group = await SendAsync(HttpMethod.Post,
            "/api/v1/resource-groups",
            new
            {
                name,
                description = "Owned realtime browser acceptance",
                tags = new[] { "e2e" }
            },
            ct,
            owner);
        var groupId = Text(group, "id");
        resources.Defer("delete realtime group and owned namespace",
            cleanup => DeleteGroupAsync(groupId, cleanup, owner));
        var targets = await SendAsync(HttpMethod.Get,
            "/api/v1/deployment-targets/available?resourceType=pole%2Fweb-service&requiresPublicIngress=false",
            null,
            ct,
            owner);
        var targetId = Text(targets.EnumerateArray().First(), "id");
        var created = await SendAsync(HttpMethod.Post,
            "/api/v1/web-services",
            new
            {
                name,
                resourceGroupId = groupId,
                exposure = "Private",
                tags = new[] { "e2e" },
                spec = Spec("initial"),
                placement = new
                {
                    targets = new[] { new { deploymentTargetId = targetId, fixedReplicas = 1 } }
                }
            },
            ct,
            owner);
        var id = Text(created, "webServiceId");
        resources.Defer("delete realtime service", cleanup => DeleteServiceAsync(id, cleanup, owner));
        return new OwnedService(id, groupId, targetId, name);
    }

    private async Task LoginAsync(IPage page, TestUser? owner = null)
    {
        owner ??= fixture.Alpha;
        await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
        await page.GetByLabel("Username or email").FillAsync(owner.Email);
        await page.GetByRole(AriaRole.Textbox,
            new PageGetByRoleOptions { Name = "Password", Exact = true }).FillAsync(owner.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
        await page.GetByLabel("Tenant").SelectOptionAsync(owner.DefaultTenantId);
    }

    private static async Task WaitForInitialBootstrapAsync(BrowserJournal journal, CancellationToken ct)
    {
        // Playwright can lose the preceding document's response body after a full navigation.
        await WaitUntilAsync(() => !journal.Bootstraps.IsEmpty || journal.ParseFailures > 0,
            TimeSpan.FromSeconds(30),
            "The login bootstrap response was not recorded before navigation.",
            ct);
        await journal.DrainAsync();
        Assert.True(journal.ParseFailures == 0, journal.DescribeParseFailures());
    }

    private string ResourceUrl(string id, string section, string? tenantId = null) => new Uri(
            fixture.FrontendBaseAddress,
            $"?view=web-services&resource={id}&section={section}&tenant={tenantId ?? fixture.Alpha.DefaultTenantId}")
        .ToString();

    private static object Spec(string marker)
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
            image = new
            {
                server = "docker.io",
                name = "library/nginx",
                tag = "1.27.5-alpine"
            },
            config = new
            {
                environmentVariables = new Dictionary<string, string> { ["REALTIME_MARKER"] = marker },
                portMappings = new[] { new { servicePort = 80, targetPort = "80", protocol = "TCP", name = "http" } }
            },
            autoscaling = (object?)null,
            probes = new
            {
                startup = probe,
                liveness = probe,
                readiness = probe
            }
        };
    }

    private async Task<JsonElement> SendAsync(HttpMethod method,
        string path,
        object? body,
        CancellationToken ct,
        TestUser? owner = null)
    {
        owner ??= fixture.Alpha;
        using var request = fixture.CreateAuthenticatedRequest(method, path, owner, owner.DefaultTenantId);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var code = await ApiProblemCode.ReadAsync(response, ct);
            Assert.Fail($"Realtime browser API {method} returned HTTP {(int)response.StatusCode}, code={code}.");
        }
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task DeleteServiceAsync(string id, CancellationToken ct, TestUser? owner = null)
    {
        owner ??= fixture.Alpha;
        await DeleteAsync($"/api/v1/web-services/{id}", ct, owner);
        await WaitUntilAsync(async probeCancellation =>
            {
                using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get,
                    $"/api/v1/web-services/{id}",
                    owner,
                    owner.DefaultTenantId);
                using var response = await fixture.Api.SendAsync(request, probeCancellation);
                return TestDeletion.IsComplete(response.StatusCode);
            },
            TimeSpan.FromMinutes(3),
            "The owned realtime service did not complete cleanup.",
            ct);
    }

    private async Task DeleteGroupAsync(string id, CancellationToken ct, TestUser? owner = null)
    {
        await DeleteAsync($"/api/v1/resource-groups/{id}", ct, owner);
        await fixture.WaitForNamespaceDeletionAsync(id, ct);
    }

    private async Task DeleteAsync(string path, CancellationToken ct, TestUser? owner = null)
    {
        owner ??= fixture.Alpha;
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, owner, owner.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound,
            $"Realtime browser cleanup returned HTTP {(int)response.StatusCode}.");
    }

    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message, CancellationToken ct) =>
        TestWait.UntilAsync(_ => Task.FromResult(condition()), timeout, message, ct);

    private static Task WaitUntilAsync(Func<CancellationToken, Task<bool>> condition,
        TimeSpan timeout,
        string message,
        CancellationToken ct) => TestWait.UntilAsync(condition, timeout, message, ct);

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

    private sealed record ChangeMetadata(
        long Timestamp,
        string ResourceId,
        long Revision);

    private sealed class BrowserJournal
    {
        private readonly ConcurrentQueue<Task> pending = new();
        private readonly RealtimeCoverageJournal coverage = new();
        private readonly HttpReadJournal<IRequest> reads = new();
        private readonly ConcurrentQueue<ChangeMetadata> changes = new();

        private static readonly Regex Jwt = new(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
            RegexOptions.CultureInvariant);

        private int connections;
        private int documentNavigations;
        private int enforceTenantReads;
        private int tenantReadScopeViolations;
        private int foreignTenantHints;
        private int refreshResponses;
        private int parseFailures;
        private readonly ConcurrentDictionary<ParseFailureKind, int> parseFailureCauses = new();
        private int unsafeUrls;
        private int unsafeHeaders;
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
        public int MetadataReadFailures => reads.Snapshot().Count(value =>
            value.Outcome is HttpReadOutcome.Rejected or HttpReadOutcome.TransportFailed);
        public int ConnectionClosures => Volatile.Read(ref connectionClosures);
        public int DocumentNavigations => Volatile.Read(ref documentNavigations);
        public int TenantReadScopeViolations => Volatile.Read(ref tenantReadScopeViolations);
        public int ForeignTenantHints => Volatile.Read(ref foreignTenantHints);
        public void TrackTenantReads() => Interlocked.Exchange(ref enforceTenantReads, 1);

        public BrowserJournal(IPage page, string tenantId)
        {
            this.tenantId = tenantId;
            page.Request += (_, request) =>
            {
                if (request.IsNavigationRequest && request.Frame == page.MainFrame)
                {
                    Interlocked.Increment(ref documentNavigations);
                    Interlocked.Exchange(ref documentStartedAt, Stopwatch.GetTimestamp());
                    coverage.Navigate();
                    reads.Navigate();
                }

                CheckUrl(request.Url);
                var path = new Uri(request.Url).AbsolutePath;
                if (request.Method == "GET" && IsPilotPath(path))
                {
                    if (Volatile.Read(ref enforceTenantReads) != 0 &&
                        (!request.Headers.TryGetValue("x-pole-tenant-id", out var requestedTenant) ||
                         requestedTenant != tenantId))
                        Interlocked.Increment(ref tenantReadScopeViolations);
                    reads.Start(request, path, Stopwatch.GetTimestamp());
                }

                if ((path == "/api/v1/realtime/session" ||
                     path.StartsWith("/api/v1/realtime/hub", StringComparison.Ordinal)) &&
                    (request.Headers.ContainsKey("x-pole-tenant-id") ||
                     path == "/api/v1/realtime/session" && request.Method == "POST" &&
                     !request.Headers.ContainsKey("authorization") ||
                     path.StartsWith("/api/v1/realtime/hub", StringComparison.Ordinal) &&
                     request.Headers.ContainsKey("authorization")))
                    Interlocked.Increment(ref unsafeHeaders);
            };
            page.RequestFinished += (_, request) => reads.Finish(request);
            page.RequestFailed += (_, request) => reads.Finish(request, failed: true,
                cancelled: request.Failure == "net::ERR_ABORTED");
            page.Response += (_, response) =>
            {
                var path = new Uri(response.Url).AbsolutePath;
                reads.Response(response.Request, response.Status);
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
                if (new Uri(socket.Url).AbsolutePath != "/api/v1/realtime/hub")
                    return;
                var connection = Interlocked.Increment(ref connections);
                var openedAt = Stopwatch.GetTimestamp();
                coverage.Open(connection, openedAt);
                socket.FrameSent += (_, frame) => RecordSent(frame.Text, connection);
                socket.Close += (_, _) =>
                {
                    coverage.Close(connection);
                    Interlocked.Increment(ref connectionClosures);
                };
                socket.FrameReceived += (_, frame) => RecordCoverage(frame.Text, connection, openedAt);
            };
        }

        private static bool IsPilotPath(string path) => path == "/api/v1/web-services" ||
                                                        path.StartsWith("/api/v1/web-services/",
                                                            StringComparison.Ordinal) &&
                                                        (!path["/api/v1/web-services/".Length..].Contains('/') ||
                                                         path.EndsWith("/releases", StringComparison.Ordinal));

        public int CountPilotReads(string detailPath, long started, long ended) => reads.Snapshot().Count(value =>
            value.StartedAt >= started && value.StartedAt <= ended && PilotPath(value.Path, detailPath));

        public bool HasDetailRead(string path, long after) => reads.Snapshot().Any(value =>
            value.Document == DocumentNavigations && value.Path == path && value.StartedAt > after && value.Outcome == HttpReadOutcome.Succeeded);

        public bool PilotReadsQuiet(string detailPath, TimeSpan quiet) =>
            reads.Quiet(path => PilotPath(path, detailPath), quiet);

        private static bool PilotPath(string path, string detailPath) => path == "/api/v1/web-services" ||
            path == detailPath || path == $"{detailPath}/releases";

        public string DescribePilotReads(string detailPath) =>
            $"Current document={DocumentNavigations}; bootstraps={Bootstraps.Count}; closes={ConnectionClosures}; refresh grants={RefreshResponses}. Recent pilot reads: " +
            string.Join("; ", reads.Snapshot().Where(value => PilotPath(value.Path, detailPath)).TakeLast(32).Select(value =>
                $"{(value.Path == "/api/v1/web-services" ? "inventory" : value.Path == detailPath ? "detail" : "releases")}: document={value.Document}, outcome={value.Outcome}, status={value.Status}, ageMs={(long)Stopwatch.GetElapsedTime(value.StartedAt).TotalMilliseconds}"));

        public bool IsQuiet(string resourceId, TimeSpan quiet) => Stopwatch.GetElapsedTime(changes
                                                                      .Where(value => value.ResourceId == resourceId)
                                                                      .Select(value => value.Timestamp).DefaultIfEmpty()
                                                                      .Max()) >=
                                                                  quiet;

        public long LastRevision(string resourceId) => changes.Where(value => value.ResourceId == resourceId)
            .Select(value => value.Revision).DefaultIfEmpty().Max();

        public int CountChanges(string resourceId, long started, long ended) => changes.Count(value =>
            value.ResourceId == resourceId && value.Timestamp >= started && value.Timestamp <= ended);

        private void CheckUrl(string url)
        {
            var queryKeys = new Uri(url).Query.TrimStart('?').Split('&')
                .Select(part => Uri.UnescapeDataString(part.Split('=', 2)[0]));
            if (Jwt.IsMatch(Uri.UnescapeDataString(url)) || queryKeys.Any(key =>
                    key is "access_token" or "refresh_token" or "id_token" or "PoleRealtime.Dev"
                        or "__Secure-PoleRealtime"))
                Interlocked.Increment(ref unsafeUrls);
        }

        public string DescribeParseFailures() => "Realtime journal parse failures: " + string.Join(
            "; ", parseFailureCauses.OrderBy(value => value.Key).Select(value => $"{value.Key}={value.Value}"));

        private void RecordParseFailure(ParseFailureKind kind)
        {
            parseFailureCauses.AddOrUpdate(kind, 1, (_, count) => count + 1);
            Interlocked.Increment(ref parseFailures);
        }

        private enum ParseFailureKind
        {
            BootstrapRequestFailed,
            BootstrapTransportAfterNavigation,
            BootstrapTransport,
            BootstrapJson,
            BootstrapSchema,
            HubBinary,
            HubJson,
            HubSchema,
            HintShape
        }

        private async Task RecordBootstrapAsync(IResponse response, long timestamp)
        {
            var responseDocument = DocumentNavigations;
            try
            {
                var value = await response.JsonAsync();
                if (value is { } session && session.TryGetProperty("expiresAtUtc", out var expires))
                    Bootstraps.Enqueue(new BootstrapMetadata(timestamp, expires.GetDateTimeOffset()));
                else
                    RecordParseFailure(ParseFailureKind.BootstrapSchema);
            }
            catch (Exception failure) when
                (failure is PlaywrightException or JsonException or InvalidOperationException)
            {
                // Fixed categories keep response/header/credential text out of diagnostics.
                RecordParseFailure(failure switch
                {
                    PlaywrightException when response.Request.Failure is not null => ParseFailureKind.BootstrapRequestFailed,
                    PlaywrightException when DocumentNavigations > responseDocument =>
                        ParseFailureKind.BootstrapTransportAfterNavigation,
                    PlaywrightException => ParseFailureKind.BootstrapTransport,
                    JsonException => ParseFailureKind.BootstrapJson,
                    _ => ParseFailureKind.BootstrapSchema
                });
            }
        }

        private void RecordSent(string? text, int connection)
        {
            if (text is null)
            {
                RecordParseFailure(ParseFailureKind.HubBinary);
                return;
            }

            foreach (var frame in text.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var document = JsonDocument.Parse(frame);
                    coverage.Sent(connection, document.RootElement);
                }
                catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException
                                                    or KeyNotFoundException)
                {
                    RecordParseFailure(failure is JsonException ? ParseFailureKind.HubJson : ParseFailureKind.HubSchema);
                }
            }
        }

        private void RecordCoverage(string? text, int connection, long openedAt)
        {
            if (text is null)
            {
                RecordParseFailure(ParseFailureKind.HubBinary);
                return;
            }

            foreach (var frame in text.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var document = JsonDocument.Parse(frame);
                    var message = document.RootElement;
                    if (!coverage.IsCurrent(connection))
                        continue;
                    coverage.Received(connection, message, Stopwatch.GetTimestamp());
                    if (message.TryGetProperty("target", out var target) && target.GetString() == "Changed")
                    {
                        var hint = message.GetProperty("arguments")[0];
                        var allowed = new[]
                        {
                            "version", "topic", "tenantId", "resourceId", "resourceGroupId", "revision", "generation",
                            "observationVersion"
                        };
                        if (hint.EnumerateObject().Any(property => !allowed.Contains(property.Name)) ||
                            hint.GetProperty("version").GetInt32() != 1)
                            RecordParseFailure(ParseFailureKind.HintShape);
                        if (Volatile.Read(ref enforceTenantReads) != 0 &&
                            openedAt > Interlocked.Read(ref documentStartedAt) &&
                            hint.TryGetProperty("tenantId", out var hintedTenant) &&
                            hintedTenant.ValueKind == JsonValueKind.String && hintedTenant.GetString() != tenantId)
                            Interlocked.Increment(ref foreignTenantHints);
                        if (hint.TryGetProperty("resourceId", out var resource) &&
                            resource.ValueKind == JsonValueKind.String &&
                            hint.TryGetProperty("tenantId", out var tenant) && tenant.GetString() == tenantId)
                        {
                            var revision =
                                hint.TryGetProperty("revision", out var version) &&
                                version.ValueKind == JsonValueKind.Number
                                    ? version.GetInt64()
                                    : 0;
                            changes.Enqueue(new ChangeMetadata(Stopwatch.GetTimestamp(),
                                resource.GetString()!,
                                revision));
                        }
                    }
                }
                catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException
                                                    or KeyNotFoundException)
                {
                    RecordParseFailure(failure is JsonException ? ParseFailureKind.HubJson : ParseFailureKind.HubSchema);
                }
            }
        }

        public bool HasCoverage(string resourceId) => LatestCoveredConnection(resourceId) > 0;

        public int LatestCoveredConnection(string resourceId, long after = 0) =>
            coverage.LatestCoveredConnection(values => new[] { "web-services", "operations", "source-builds" }
                .All(topic => values.Any(value => value.Covered && value.Topic == topic &&
                    value.ResourceId == resourceId && value.TenantId == tenantId)), after, requireOpenedAfter: true);

        public Task DrainAsync() => Task.WhenAll(pending.ToArray());
    }
}
