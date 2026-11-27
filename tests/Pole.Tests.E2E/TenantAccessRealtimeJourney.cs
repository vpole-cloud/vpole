// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Features.Core.Multitenancy;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using static Pole.Tests.E2E.Infrastructure.RealtimeBrowserAssertions;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class TenantAccessRealtimeJourney(
    PoleE2EFixture fixture,
    ITestOutputHelper output
)
{
    private readonly TestTimings _timings = new(typeof(TenantAccessRealtimeJourney));

    [Fact]
    public async Task Tenant_tabs_and_membership_changes_preserve_realtime_workspace_isolation()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence
            = new TestPhaseJournal(nameof(Tenant_tabs_and_membership_changes_preserve_realtime_workspace_isolation))
                .Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = budget.Token;
        var resources = new TestResourceScope("tenant access realtime journey", timings: _timings);
        await resources.RunAsync(async () =>
        {
            using var tabsPhase = _timings.Measure("e2e.tenant-access.tabs");
            using var setupPhase = _timings.Measure("e2e.setup");
            var alphaTenant = fixture.Alpha.DefaultTenantId;
            var betaTenant = fixture.Beta.DefaultTenantId;
            var memberId = Text(
                await fixture.InviteAndJoinAsync(alphaTenant, fixture.Alpha, fixture.Beta, TenantRole.Member, ct),
                "id");
            resources.Defer(
                "remove owned realtime Alpha membership",
                cleanup => memberId is null ? Task.CompletedTask : DeleteMemberAsync(alphaTenant, memberId, cleanup));
            var alpha = await CreateOwnedServiceAsync(resources, ct);
            var beta = await CreateOwnedServiceAsync(resources, ct, fixture.Beta);
            await fixture.WaitForNamespaceCreationAsync(alpha.GroupId, ct);
            await fixture.WaitForNamespaceCreationAsync(beta.GroupId, ct, fixture.Beta);
            await fixture.WaitForWebServiceReadyAsync(alpha.GroupId, alpha.Name, ct);
            await fixture.WaitForWebServiceReadyAsync(beta.GroupId, beta.Name, ct);
            await fixture.UseInteractiveClientTokenLifespanAsync(resources, TimeSpan.FromMinutes(5), ct);
            setupPhase.Dispose();
            using var browserAcquisition = _timings.Measure("e2e.browser.acquire");
            var browserEndpoint = await fixture.GetBrowserEndpointAsync(ct);
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(
                browserEndpoint,
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            var context = await browser.NewContextAsync(
                new BrowserNewContextOptions
                {
                    ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                    IgnoreHTTPSErrors = true,
                });
            await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
            var alphaPage = await BrowserEvidence.NewPageAsync(context);
            var alphaJournal = new RealtimeBrowserJournal(alphaPage, alphaTenant);
            var membershipJournal = new MembershipJournal(alphaPage, fixture.Beta.KeycloakUserId, alphaTenant);
            browserAcquisition.Dispose();
            await LoginAsync(alphaPage, fixture.Beta);
            await WaitForInitialBootstrapAsync(alphaJournal, ct);
            await alphaPage.GotoAsync(ResourceUrl(alpha.Id, "configuration", alphaTenant));
            await Assertions.Expect(alphaPage.GetByLabel("Environment variables")).ToBeVisibleAsync();
            await WaitUntilAsync(
                () => alphaJournal.HasCoverage(alpha.Id),
                TimeSpan.FromSeconds(30),
                "The shared account did not acknowledge its Alpha resource.",
                ct);
            const string alphaDraft = "REALTIME_ALPHA_TAB_DRAFT=unsaved";
            await alphaPage.GetByLabel("Environment variables").FillAsync(alphaDraft);
            var alphaEditor = await alphaPage.GetByLabel("Environment variables").ElementHandleAsync() ??
                throw new InvalidOperationException("The Alpha dirty editor is unavailable.");
            alphaJournal.TrackTenantReads();
            var betaPage = await BrowserEvidence.NewPageAsync(context);
            var betaJournal = new RealtimeBrowserJournal(betaPage, betaTenant);
            // Same browser context: the real Keycloak SSO and HttpOnly realtime cookie are shared.
            await betaPage.GotoAsync(ResourceUrl(beta.Id, "configuration", betaTenant));
            await Assertions.Expect(betaPage.GetByLabel("Environment variables"))
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30000 });
            await WaitUntilAsync(
                () => betaJournal.HasCoverage(beta.Id),
                TimeSpan.FromSeconds(30),
                "The shared account did not acknowledge its Beta resource.",
                ct);
            const string betaDraft = "REALTIME_BETA_TAB_DRAFT=unsaved";
            await betaPage.GetByLabel("Environment variables").FillAsync(betaDraft);
            var betaEditor = await betaPage.GetByLabel("Environment variables").ElementHandleAsync() ??
                throw new InvalidOperationException("The Beta dirty editor is unavailable.");
            betaJournal.TrackTenantReads();
            var alphaNavigation = alphaJournal.DocumentNavigations;
            var betaNavigation = betaJournal.DocumentNavigations;
            await alphaPage.BringToFrontAsync();
            await ClickResourceSectionAsync(alphaPage, alpha.Name, "Overview");
            await WaitUntilAsync(
                () => alphaJournal.HasCoverage(alpha.Id),
                TimeSpan.FromSeconds(30),
                "Activating Alpha did not restore its own acknowledged coverage.",
                ct);
            Assert.Equal(2, await UpdateMetadataAsync(alpha.Id, 1, "alpha-tab", ct));
            await WaitUntilAsync(
                () => alphaJournal.LastRevision(alpha.Id) >= 2,
                TimeSpan.FromSeconds(20),
                "The committed Alpha change did not reach its owning tab.",
                ct);
            await Assertions.Expect(ActiveRevision(alphaPage)).ToHaveTextAsync("2");
            await betaPage.BringToFrontAsync();
            await ClickResourceSectionAsync(betaPage, beta.Name, "Overview");
            await WaitUntilAsync(
                () => betaJournal.HasCoverage(beta.Id),
                TimeSpan.FromSeconds(30),
                "Activating Beta did not restore its own acknowledged coverage.",
                ct);
            await Assertions.Expect(ActiveRevision(betaPage)).ToHaveTextAsync("1");
            Assert.Equal(2, await UpdateMetadataAsync(beta.Id, 1, "beta-tab-2", ct, owner: fixture.Beta));
            Assert.Equal(3, await UpdateMetadataAsync(beta.Id, 2, "beta-tab-3", ct, owner: fixture.Beta));
            await WaitUntilAsync(
                () => betaJournal.LastRevision(beta.Id) >= 3,
                TimeSpan.FromSeconds(20),
                "The committed Beta change did not reach its owning tab.",
                ct);
            await Assertions.Expect(ActiveRevision(betaPage)).ToHaveTextAsync("3");
            await alphaPage.BringToFrontAsync();
            await WaitUntilAsync(
                () => alphaJournal.HasCoverage(alpha.Id),
                TimeSpan.FromSeconds(30),
                "Returning to Alpha did not retain its independent resource coverage.",
                ct);
            await Assertions.Expect(ActiveRevision(alphaPage)).ToHaveTextAsync("2");
            foreach (var (page, journal, owned, tenantId, draft, editor, navigation) in new[]
                     {
                         (alphaPage, alphaJournal, alpha, alphaTenant, alphaDraft, alphaEditor, alphaNavigation),
                         (betaPage, betaJournal, beta, betaTenant, betaDraft, betaEditor, betaNavigation),
                     })
            {
                await page.BringToFrontAsync();
                await ClickResourceSectionAsync(page, owned.Name, "Configuration");
                Assert.Equal(tenantId, await page.GetByLabel("Tenant").InputValueAsync());
                Assert.Equal(draft, await page.GetByLabel("Environment variables").InputValueAsync());
                Assert.True(
                    await page.GetByLabel("Environment variables")
                        .EvaluateAsync<bool>("(element, original) => element === original", editor));
                var foreign = owned == alpha ? beta : alpha;
                Assert.Equal(
                    0,
                    await page.GetByRole(
                            AriaRole.Heading,
                            new PageGetByRoleOptions { Name = foreign.Name, Exact = true })
                        .CountAsync());
                var safeWorkspace = await page.EvaluateAsync<bool>(
                    """
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

            tabsPhase.Dispose();
            await betaPage.CloseAsync();
            await membershipJournal.DrainAsync();
            using (_timings.Measure("e2e.tenant-access.membership"))
            {
                var page = alphaPage;
                var journal = membershipJournal;
                var tenantId = alphaTenant;
                var owned = alpha;
                // The tab phase leaves this same Alpha page in Configuration. Retain its
                // session and replace the already-verified draft without another navigation.
                await Assertions.Expect(
                        page.GetByRole(
                            AriaRole.Heading,
                            new PageGetByRoleOptions { Name = "Service configuration", Exact = true }))
                    .ToBeVisibleAsync();
                await WaitUntilAsync(
                    () => journal.HasOwnCoverage(),
                    TimeSpan.FromSeconds(30),
                    "The real browser did not acknowledge its own-user tenant-access subscription.",
                    ct);
                var tenant = page.GetByLabel("Tenant");
                Assert.Equal(tenantId, await tenant.InputValueAsync());
                const string draft = "REALTIME_MEMBERSHIP_DRAFT=unsaved";
                await page.GetByLabel("Environment variables").FillAsync(draft);
                await Assertions.Expect(
                        page.GetByRole(
                            AriaRole.Button,
                            new PageGetByRoleOptions { Name = "Save changes", Exact = true }))
                    .ToBeEnabledAsync();
                var content = page.Locator("#app-content");
                var configuration = content.GetByRole(
                    AriaRole.Tab,
                    new LocatorGetByRoleOptions { Name = "Configuration", Exact = true });
                await Assertions.Expect(configuration).ToHaveAttributeAsync("aria-selected", "true");
                var navigationCount = journal.DocumentNavigations;
                var refreshCount = journal.RefreshGrants;

                // Capture a real Member discovery response before committing the demotion. The
                // subsequent own-user hint must fence its GET-start epoch, even while it is held.
                const string discoveryRoute = "**/api/v1/tenants";
                var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                IRequest? heldRequest = null;
                var intercepted = 0;
                var canceled = 0;
                page.RequestFailed += (_, request) =>
                {
                    if (ReferenceEquals(request, heldRequest))
                        Interlocked.Increment(ref canceled);
                };
                await page.RouteAsync(
                    discoveryRoute,
                    async route =>
                    {
                        if (route.Request.Method != "GET" || Interlocked.CompareExchange(ref intercepted, 1, 0) != 0)
                        {
                            await route.ContinueAsync();
                            return;
                        }

                        heldRequest = route.Request;
                        try
                        {
                            var response = await route.FetchAsync();
                            try
                            {
                                Assert.True(
                                    response.Ok,
                                    "The delayed discovery response must come from the authenticated API.");
                                using var body = JsonDocument.Parse(await response.TextAsync());
                                Assert.True(
                                    HasRole(body.RootElement, tenantId, TenantRole.Member),
                                    "The delayed response must have been fetched before demotion.");
                                held.TrySetResult();
                                await release.Task.WaitAsync(ct);
                                await route.FulfillAsync(new RouteFulfillOptions { Response = response });
                                delivered.TrySetResult();
                            }
                            finally
                            {
                                await response.DisposeAsync();
                            }
                        }
                        catch (PlaywrightException)
                        {
                            // Fetch diagnostics can contain bearer headers; keep a failing, fixed-message
                            // result instead of exporting credential-bearing transport diagnostics.
                            var failure = new InvalidOperationException(
                                "The controlled authenticated discovery request failed.");
                            held.TrySetException(failure);
                            delivered.TrySetException(failure);
                        }
                        catch (Exception failure)
                        {
                            held.TrySetException(failure);
                            delivered.TrySetException(failure);
                        }
                    });
                try
                {
                    await page.EvaluateAsync("window.dispatchEvent(new Event('online'))");
                    await held.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                    var previousHints = journal.OwnHints;
                    await SendAsync(
                        HttpMethod.Put,
                        $"/api/v1/tenants/{tenantId}/members/{memberId}",
                        fixture.Alpha,
                        null,
                        new { role = TenantRole.Viewer.ToString() },
                        ct);
                    await WaitUntilAsync(
                        () => journal.OwnHints > previousHints,
                        TimeSpan.FromSeconds(15),
                        "The committed Viewer role did not deliver an own-user change hint.",
                        ct);
                    var roleHint = journal.LastOwnHint;
                    release.TrySetResult();
                    await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                    await WaitUntilAsync(
                        () => journal.HasFreshRole(TenantRole.Viewer, roleHint),
                        TimeSpan.FromSeconds(15),
                        "A post-hint authoritative discovery read did not confirm Viewer access.",
                        ct);
                    await Assertions.Expect(
                            content.GetByText(
                                "Tenant viewer access is read-only.",
                                new LocatorGetByTextOptions { Exact = true }))
                        .ToBeVisibleAsync();
                    Assert.Equal(0, await page.GetByLabel("Environment variables").CountAsync());
                    Assert.Equal(tenantId, await tenant.InputValueAsync());
                    await Assertions.Expect(configuration).ToHaveAttributeAsync("aria-selected", "true");
                    Assert.Equal(0, Volatile.Read(ref canceled));
                }
                finally
                {
                    release.TrySetResult();
                    await page.UnrouteAsync(discoveryRoute);
                }

                await content.GetByRole(
                        AriaRole.Tab,
                        new LocatorGetByRoleOptions { Name = "All services", Exact = true })
                    .ClickAsync();
                await Assertions.Expect(content.GetByTestId($"resource-row-{owned.Id}")).ToBeVisibleAsync();
                await Assertions.Expect(
                        content.GetByRole(
                            AriaRole.Button,
                            new LocatorGetByRoleOptions { Name = "New service", Exact = true }))
                    .ToBeDisabledAsync();
                await Assertions.Expect(
                        content.GetByRole(
                            AriaRole.Button,
                            new LocatorGetByRoleOptions { Name = $"Delete {owned.Name}", Exact = true }))
                    .ToBeDisabledAsync();
                using (var forbidden = fixture.CreateAuthenticatedRequest(
                           HttpMethod.Delete,
                           $"/api/v1/web-services/{owned.Id}",
                           fixture.Beta,
                           tenantId))
                using (var result = await fixture.Api.SendAsync(forbidden, ct))
                {
                    Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
                }

                var hintsBeforeRemoval = journal.OwnHints;
                await DeleteMemberAsync(tenantId, memberId!, ct);
                memberId = null;
                await WaitUntilAsync(
                    () => journal.OwnHints > hintsBeforeRemoval,
                    TimeSpan.FromSeconds(15),
                    "Membership removal did not reach the user's independent notification subscription.",
                    ct);
                var removalHint = journal.LastOwnHint;
                await WaitUntilAsync(
                    () => journal.HasFreshAbsence(removalHint),
                    TimeSpan.FromSeconds(15),
                    "A post-removal discovery read did not remove the inaccessible tenant.",
                    ct);
                // An explicit URL for the removed tenant must require a new selection rather
                // than silently reinterpret the resource link in another tenant.
                await Assertions.Expect(tenant).ToHaveValueAsync("");
                await Assertions.Expect(
                        content.GetByText(
                            "This tenant is unavailable to your account. Select an available tenant.",
                            new LocatorGetByTextOptions { Exact = true }))
                    .ToBeVisibleAsync();
                await Assertions.Expect(tenant.Locator($"option[value='{tenantId}']")).ToHaveCountAsync(0);
                await Assertions.Expect(content.GetByTestId($"resource-row-{owned.Id}")).ToHaveCountAsync(0);
                Assert.False(
                    (await content.InnerTextAsync()).Contains(owned.Name, StringComparison.Ordinal),
                    "Removed tenant data must leave the visible workspace.");
                await WaitUntilAsync(
                    () => journal.HasOwnCoverage(removalHint),
                    TimeSpan.FromSeconds(20),
                    "Removing tenant access must preserve the own-user subscription.",
                    ct);
                using (var rejected = fixture.CreateAuthenticatedRequest(
                           HttpMethod.Get,
                           $"/api/v1/web-services/{owned.Id}",
                           fixture.Beta,
                           tenantId))
                using (var result = await fixture.Api.SendAsync(rejected, ct))
                {
                    Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
                }

                await tenant.SelectOptionAsync(fixture.Beta.DefaultTenantId);
                await Assertions.Expect(tenant).ToHaveValueAsync(fixture.Beta.DefaultTenantId);
                var safeWorkspace = await page.EvaluateAsync<bool>(
                    """
                    ([tenant, resource]) => {
                      const records = Object.entries(localStorage).filter(([key]) => key.endsWith('.' + tenant));
                      return records.length > 0 && records.every(([, value]) => !value.includes(resource));
                    }
                    """,
                    new[] { fixture.Beta.DefaultTenantId, owned.Id });
                Assert.True(
                    safeWorkspace,
                    "The selected tenant's persisted workspace must exclude the removed tenant's resource.");

                // Beta has no access to Alpha here. Its account-scoped subscription must still learn
                // that membership was restored; only the subsequent HTTP read can restore write access.
                var hintsBeforeRestore = journal.OwnHints;
                memberId = Text(
                    await fixture.InviteAndJoinAsync(tenantId, fixture.Alpha, fixture.Beta, TenantRole.Member, ct),
                    "id");
                await WaitUntilAsync(
                    () => journal.OwnHints > hintsBeforeRestore,
                    TimeSpan.FromSeconds(15),
                    "Restored membership did not reach the own-user subscription without tenant access.",
                    ct);
                var restoredHint = journal.LastOwnHint;
                await WaitUntilAsync(
                    () => journal.HasFreshRole(TenantRole.Member, restoredHint),
                    TimeSpan.FromSeconds(15),
                    "Write access must return through a fresh authoritative membership read.",
                    ct);
                await Assertions.Expect(tenant.Locator($"option[value='{tenantId}']")).ToHaveCountAsync(1);
                await tenant.SelectOptionAsync(tenantId);
                await Assertions.Expect(
                        content.GetByRole(
                            AriaRole.Button,
                            new LocatorGetByRoleOptions { Name = "New service", Exact = true }))
                    .ToBeEnabledAsync();
                await content.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = owned.Name, Exact = true })
                    .ClickAsync();
                await content.GetByRole(
                        AriaRole.Tab,
                        new LocatorGetByRoleOptions { Name = "Configuration", Exact = true })
                    .ClickAsync();
                await Assertions.Expect(page.GetByLabel("Environment variables")).ToBeEnabledAsync();
                Assert.DoesNotContain(
                    "REALTIME_MEMBERSHIP_DRAFT",
                    await page.GetByLabel("Environment variables").InputValueAsync());
                Assert.Equal(tenantId, await tenant.InputValueAsync());
                Assert.Equal(navigationCount, journal.DocumentNavigations);
                Assert.Equal(refreshCount, journal.RefreshGrants);
                Assert.Equal(0, journal.SessionRevocations);
                Assert.Equal(0, journal.ForeignUserHints);
                await journal.DrainAsync();
                Assert.Equal(0, journal.ParseFailures);
                Assert.Equal(0, journal.DiscoveryReadFailures);
                Assert.Equal(0, journal.TenantHeadersOnDiscovery);
            }
        });
    }

    private static ILocator ActiveRevision(IPage page) => page
        .GetByText("Active revision", new PageGetByTextOptions { Exact = true })
        .Locator("xpath=following-sibling::dd");

    private static Task ClickResourceSectionAsync(
        IPage page,
        string name,
        string section) =>
        page.GetByRole(AriaRole.Tablist, new PageGetByRoleOptions { Name = $"{name} sections", Exact = true })
            .GetByRole(AriaRole.Tab, new LocatorGetByRoleOptions { Name = section, Exact = true })
            .ClickAsync();

    private async Task<long> UpdateMetadataAsync(
        string id,
        long expectedRevision,
        string marker,
        CancellationToken ct,
        Uri? apiAddress = null,
        TestUser? owner = null)
    {
        var path = $"/api/v1/web-services/{id}";
        output.WriteLine($"Metadata update: expectedRevision={expectedRevision}, step={marker}.");
        var accepted = await SendAsync(
            HttpMethod.Put,
            apiAddress is null ? path : new Uri(apiAddress, path).ToString(),
            new
            {
                spec = Spec("initial"),
                tags = new[] { "e2e", marker },
                exposure = "Private",
                expectedRevision,
            },
            ct,
            owner);
        return accepted.GetProperty("resourceRevision").GetInt64();
    }

    private async Task<OwnedService> CreateOwnedServiceAsync(
        TestResourceScope resources,
        CancellationToken ct,
        TestUser? owner = null)
    {
        owner ??= fixture.Alpha;
        var name = $"realtime-{Guid.NewGuid():N}"[..20];
        var group = await SendAsync(
            HttpMethod.Post,
            "/api/v1/resource-groups",
            new
            {
                name,
                description = "Owned realtime browser acceptance",
                tags = new[] { "e2e" },
            },
            ct,
            owner);
        var groupId = Text(group, "id");
        resources.Defer(
            "delete realtime group and owned namespace",
            cleanup => DeleteGroupAsync(groupId, cleanup, owner));
        var targets = await SendAsync(
            HttpMethod.Get,
            "/api/v1/deployment-targets/available?resourceType=pole%2Fweb-service&requiresPublicIngress=false",
            null,
            ct,
            owner);
        var targetId = Text(targets.EnumerateArray().First(), "id");
        var created = await SendAsync(
            HttpMethod.Post,
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
                    targets = new[] { new { deploymentTargetId = targetId, fixedReplicas = 1 } },
                },
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
        await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true })
            .FillAsync(owner.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
        await page.GetByLabel("Tenant").SelectOptionAsync(owner.DefaultTenantId);
    }

    private string ResourceUrl(
        string id,
        string section,
        string? tenantId = null) => new Uri(
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
            successThreshold = 1,
        };
        return new
        {
            image = new
            {
                server = "docker.io",
                name = "library/nginx",
                tag = "1.27.5-alpine",
            },
            config = new
            {
                environmentVariables = new Dictionary<string, string> { ["REALTIME_MARKER"] = marker },
                portMappings = new[] { new { servicePort = 80, targetPort = "80", protocol = "TCP", name = "http" } },
            },
            autoscaling = (object?)null,
            probes = new
            {
                startup = probe,
                liveness = probe,
                readiness = probe,
            },
        };
    }

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
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

    private async Task DeleteServiceAsync(
        string id,
        CancellationToken ct,
        TestUser? owner = null)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteServiceAsync");
        owner ??= fixture.Alpha;
        await DeleteAsync($"/api/v1/web-services/{id}", ct, owner);
        await WaitUntilAsync(
            async probeCancellation =>
            {
                using var request = fixture.CreateAuthenticatedRequest(
                    HttpMethod.Get,
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

    private async Task DeleteGroupAsync(
        string id,
        CancellationToken ct,
        TestUser? owner = null)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteGroupAsync");
        await DeleteAsync($"/api/v1/resource-groups/{id}", ct, owner);
        await fixture.WaitForNamespaceDeletionAsync(id, ct);
    }

    private async Task DeleteAsync(
        string path,
        CancellationToken ct,
        TestUser? owner = null)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteAsync");
        owner ??= fixture.Alpha;
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, owner, owner.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound,
            $"Realtime browser cleanup returned HTTP {(int)response.StatusCode}.");
    }

    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;

    private static Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string message,
        CancellationToken ct) => TestWait.UntilAsync(_ => Task.FromResult(condition()), timeout, message, ct);

    private static Task WaitUntilAsync(
        Func<CancellationToken, Task<bool>> condition,
        TimeSpan timeout,
        string message,
        CancellationToken ct) => TestWait.UntilAsync(condition, timeout, message, ct);

    private Task DeleteMemberAsync(
        string tenantId,
        string memberId,
        CancellationToken ct) => DeleteAsync($"/api/v1/tenants/{tenantId}/members/{memberId}", null, ct);

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        TestUser user,
        string? tenantId,
        object? body,
        CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(method, path, user, tenantId);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Realtime membership API {method} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task DeleteAsync(
        string path,
        string? tenantId,
        CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteAsync");
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, fixture.Alpha, tenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(
            response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound,
            $"Realtime membership cleanup returned HTTP {(int)response.StatusCode}.");
    }

    private static bool HasRole(
        JsonElement tenants,
        string tenantId,
        TenantRole role) => tenants.EnumerateArray()
        .Any(tenant => Text(tenant, "id") == tenantId && Text(tenant, "role") == role.ToString());

    private sealed record OwnedService(
        string Id,
        string GroupId,
        string TargetId,
        string Name
    );

    private sealed record MembershipRead(
        long StartedAt,
        JsonElement Tenants
    );

    private sealed class MembershipJournal
    {
        private readonly RealtimeCoverageJournal coverage = new();
        private readonly HttpReadJournal<IRequest> discoveryReads = new();
        private readonly ConcurrentDictionary<IRequest, (long StartedAt, int Document)> discoveryStarts = new();
        private readonly ConcurrentQueue<MembershipRead> membershipReads = new();
        private readonly ConcurrentQueue<Task> pending = new();
        private readonly string subject;
        private readonly string tenantId;
        private int connections;
        private int documentNavigations;
        private int foreignUserHints;
        private long lastOwnHint;
        private int ownHints;
        private int parseFailures;
        private int refreshGrants;
        private int sessionRevocations;
        private int tenantHeadersOnDiscovery;

        public MembershipJournal(
            IPage page,
            string subject,
            string tenantId)
        {
            this.subject = subject;
            this.tenantId = tenantId;
            page.Request += (_, request) =>
            {
                if (request.IsNavigationRequest && request.Frame == page.MainFrame)
                {
                    Interlocked.Increment(ref documentNavigations);
                    discoveryStarts.Clear();
                    coverage.Navigate();
                    discoveryReads.Navigate();
                }

                if (request.Method == "GET" && new Uri(request.Url).AbsolutePath == "/api/v1/tenants")
                {
                    var started = Stopwatch.GetTimestamp();
                    discoveryStarts[request] = (started, DocumentNavigations);
                    discoveryReads.Start(request, "/api/v1/tenants", started);
                    if (request.Headers.ContainsKey("x-pole-tenant-id"))
                        Interlocked.Increment(ref tenantHeadersOnDiscovery);
                }

                if (request.Method == "POST" &&
                    new Uri(request.Url).AbsolutePath.EndsWith(
                        "/protocol/openid-connect/token",
                        StringComparison.Ordinal) && request.PostData?.Contains(
                        "grant_type=refresh_token",
                        StringComparison.Ordinal) == true)
                    Interlocked.Increment(ref refreshGrants);
            };
            page.Response += (_, response) =>
            {
                discoveryReads.Response(response.Request, response.Status);
                if (response.Ok && response.Request.Method == "GET" &&
                    new Uri(response.Url).AbsolutePath == "/api/v1/tenants" &&
                    discoveryStarts.TryGetValue(response.Request, out var started))
                    pending.Enqueue(RecordReadAsync(response, started.StartedAt, started.Document));
            };
            page.RequestFinished += (_, request) =>
            {
                discoveryReads.Finish(request);
                FinishRead(request);
            };
            page.RequestFailed += (_, request) =>
            {
                discoveryReads.Finish(request, failed: true, cancelled: request.Failure == "net::ERR_ABORTED");
                FinishRead(request);
            };
            page.WebSocket += (_, socket) =>
            {
                if (new Uri(socket.Url).AbsolutePath != "/api/v1/realtime/hub")
                    return;
                var connection = Interlocked.Increment(ref connections);
                coverage.Open(connection, Stopwatch.GetTimestamp());
                socket.Close += (_, _) => coverage.Close(connection);
                socket.FrameSent += (_, frame) => ReadFrames(frame.Text, message => coverage.Sent(connection, message));
                socket.FrameReceived += (_, frame) => ReadFrames(
                    frame.Text,
                    message =>
                    {
                        if (!coverage.IsCurrent(connection))
                            return;
                        coverage.Received(connection, message, Stopwatch.GetTimestamp());
                        RecordMessage(message);
                    });
            };
        }

        public int DocumentNavigations => Volatile.Read(ref documentNavigations);
        public int RefreshGrants => Volatile.Read(ref refreshGrants);
        public int OwnHints => Volatile.Read(ref ownHints);
        public long LastOwnHint => Interlocked.Read(ref lastOwnHint);
        public int SessionRevocations => Volatile.Read(ref sessionRevocations);
        public int ForeignUserHints => Volatile.Read(ref foreignUserHints);
        public int ParseFailures => Volatile.Read(ref parseFailures);

        public int DiscoveryReadFailures =>
            discoveryReads.Snapshot()
                .Count(value => value.Outcome is HttpReadOutcome.Rejected or HttpReadOutcome.TransportFailed);

        public int TenantHeadersOnDiscovery => Volatile.Read(ref tenantHeadersOnDiscovery);

        public bool HasOwnCoverage(long after = 0) => coverage.LatestCoveredConnection(
            values => values.Any(value =>
                value.Covered && value.Topic == "tenant-access" && value.ResourceId == subject &&
                value.TenantId is null),
            after) > 0;

        public bool HasFreshRole(TenantRole role, long after) => membershipReads.Any(value =>
            value.StartedAt > after && HasRole(value.Tenants, tenantId, role));

        public bool HasFreshAbsence(long after) => membershipReads.Any(value => value.StartedAt > after &&
            value.Tenants.EnumerateArray().All(tenant => Text(tenant, "id") != tenantId));

        public Task DrainAsync() => Task.WhenAll(pending.ToArray());

        private void FinishRead(IRequest request) => discoveryStarts.TryRemove(request, out _);

        private async Task RecordReadAsync(
            IResponse response,
            long started,
            int requestDocument)
        {
            try
            {
                var value = await response.JsonAsync();
                if (value is not { ValueKind: JsonValueKind.Array } tenants)
                {
                    Interlocked.Increment(ref parseFailures);
                    return;
                }

                if (requestDocument == DocumentNavigations)
                    membershipReads.Enqueue(new MembershipRead(started, tenants.Clone()));
            }
            catch (Exception failure) when
                (failure is PlaywrightException or JsonException or InvalidOperationException)
            {
                Interlocked.Increment(ref parseFailures);
            }
        }

        private void ReadFrames(string? text, Action<JsonElement> read)
        {
            if (text is null)
            {
                Interlocked.Increment(ref parseFailures);
                return;
            }

            foreach (var frame in text.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                try
                {
                    using var document = JsonDocument.Parse(frame);
                    read(document.RootElement);
                }
                catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException
                                                    or KeyNotFoundException)
                {
                    Interlocked.Increment(ref parseFailures);
                }
        }

        private void RecordMessage(JsonElement message)
        {
            if (message.TryGetProperty("target", out var target))
            {
                if (target.GetString() == "SessionRevoked")
                    Interlocked.Increment(ref sessionRevocations);
                if (target.GetString() == "Changed")
                {
                    var hint = message.GetProperty("arguments")[0];
                    if (Text(hint, "topic") == "tenant-access")
                    {
                        var allowed = new[]
                        {
                            "version", "topic", "tenantId", "resourceId", "resourceGroupId", "revision", "generation",
                            "observationVersion",
                        };
                        if (hint.EnumerateObject().Any(property => !allowed.Contains(property.Name)) ||
                            hint.GetProperty("version").GetInt32() != 1 ||
                            (hint.TryGetProperty("tenantId", out var tenant) && tenant.ValueKind != JsonValueKind.Null))
                            Interlocked.Increment(ref parseFailures);
                        if (Text(hint, "resourceId") != subject)
                        {
                            Interlocked.Increment(ref foreignUserHints);
                        }
                        else
                        {
                            Interlocked.Exchange(ref lastOwnHint, Stopwatch.GetTimestamp());
                            Interlocked.Increment(ref ownHints);
                        }
                    }
                }
            }
        }
    }
}
