using System.Collections.Concurrent;
using System.Diagnostics;
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
public sealed class RealtimeMembershipE2ETests(PoleE2EFixture fixture)
{
    [Fact]
    public async Task Own_user_notifications_revalidate_roles_and_restore_removed_membership_without_token_rotation()
    {
        using var testEvidence = new TestPhaseJournal(nameof(Own_user_notifications_revalidate_roles_and_restore_removed_membership_without_token_rotation)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = budget.Token;
        var resources = new TestResourceScope("realtime membership browser E2E");
        await resources.RunAsync(async () =>
        {
            var tenantId = fixture.Alpha.DefaultTenantId;
            var subject = fixture.Beta.KeycloakUserId;
            string? memberId = Text(await ManageMemberAsync(HttpMethod.Post, tenantId, null, TenantRole.Member, ct), "id");
            resources.Defer("remove temporary realtime member",
                cleanup => memberId is null
                    ? Task.CompletedTask
                    : DeleteMemberAsync(tenantId, memberId, cleanup));
            var owned = await CreateOwnedServiceAsync(resources, tenantId, ct);
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
            var page = await BrowserEvidence.NewPageAsync(context);
            var journal = new MembershipJournal(page, subject, tenantId);
            await LoginAsync(page);
            await page.GotoAsync(new Uri(fixture.FrontendBaseAddress,
                $"?view=web-services&resource={owned.Id}&section=configuration&tenant={tenantId}").ToString());
            await Assertions.Expect(page.GetByRole(AriaRole.Heading,
                new PageGetByRoleOptions { Name = "Service configuration", Exact = true })).ToBeVisibleAsync();
            await WaitUntilAsync(() => journal.HasOwnCoverage(),
                TimeSpan.FromSeconds(30),
                "The real browser did not acknowledge its own-user tenant-access subscription.",
                ct);
            var tenant = page.GetByLabel("Tenant");
            Assert.Equal(tenantId, await tenant.InputValueAsync());
            const string draft = "REALTIME_MEMBERSHIP_DRAFT=unsaved";
            await page.GetByLabel("Environment variables").FillAsync(draft);
            await Assertions.Expect(page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Save changes", Exact = true })).ToBeEnabledAsync();
            var content = page.Locator("#app-content");
            var configuration = content.GetByRole(AriaRole.Tab,
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
            await page.RouteAsync(discoveryRoute,
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
                            Assert.True(response.Ok,
                                "The delayed discovery response must come from the authenticated API.");
                            using var body = JsonDocument.Parse(await response.TextAsync());
                            Assert.True(HasRole(body.RootElement, tenantId, TenantRole.Member),
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
                        var failure =
                            new InvalidOperationException("The controlled authenticated discovery request failed.");
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
                await ManageMemberAsync(HttpMethod.Put, tenantId, memberId, TenantRole.Viewer, ct);
                await WaitUntilAsync(() => journal.OwnHints > previousHints,
                    TimeSpan.FromSeconds(15),
                    "The committed Viewer role did not deliver an own-user change hint.",
                    ct);
                var roleHint = journal.LastOwnHint;
                release.TrySetResult();
                await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                await WaitUntilAsync(() => journal.HasFreshRole(TenantRole.Viewer, roleHint),
                    TimeSpan.FromSeconds(15),
                    "A post-hint authoritative discovery read did not confirm Viewer access.",
                    ct);
                await Assertions.Expect(content.GetByText("Tenant viewer access is read-only.",
                    new LocatorGetByTextOptions { Exact = true })).ToBeVisibleAsync();
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

            await content.GetByRole(AriaRole.Tab,
                new LocatorGetByRoleOptions { Name = "All services", Exact = true }).ClickAsync();
            await Assertions.Expect(content.GetByTestId($"resource-row-{owned.Id}")).ToBeVisibleAsync();
            await Assertions.Expect(content.GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "New service", Exact = true })).ToBeDisabledAsync();
            await Assertions.Expect(content.GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = $"Delete {owned.Name}", Exact = true })).ToBeDisabledAsync();
            using (var forbidden = fixture.CreateAuthenticatedRequest(HttpMethod.Delete,
                       $"/api/v1/web-services/{owned.Id}",
                       fixture.Beta,
                       tenantId))
            using (var result = await fixture.Api.SendAsync(forbidden, ct))
                Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);

            var hintsBeforeRemoval = journal.OwnHints;
            await DeleteMemberAsync(tenantId, memberId!, ct);
            memberId = null;
            await WaitUntilAsync(() => journal.OwnHints > hintsBeforeRemoval,
                TimeSpan.FromSeconds(15),
                "Membership removal did not reach the user's independent notification subscription.",
                ct);
            var removalHint = journal.LastOwnHint;
            await WaitUntilAsync(() => journal.HasFreshAbsence(removalHint),
                TimeSpan.FromSeconds(15),
                "A post-removal discovery read did not remove the inaccessible tenant.",
                ct);
            // An explicit URL for the removed tenant must require a new selection rather
            // than silently reinterpret the resource link in another tenant.
            await Assertions.Expect(tenant).ToHaveValueAsync("");
            await Assertions.Expect(content.GetByText(
                "This tenant is unavailable to your account. Select an available tenant.",
                new LocatorGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(tenant.Locator($"option[value='{tenantId}']")).ToHaveCountAsync(0);
            await Assertions.Expect(content.GetByTestId($"resource-row-{owned.Id}")).ToHaveCountAsync(0);
            Assert.False((await content.InnerTextAsync()).Contains(owned.Name, StringComparison.Ordinal),
                "Removed tenant data must leave the visible workspace.");
            await WaitUntilAsync(() => journal.HasOwnCoverage(removalHint),
                TimeSpan.FromSeconds(20),
                "Removing tenant access must preserve the own-user subscription.",
                ct);
            using (var rejected = fixture.CreateAuthenticatedRequest(HttpMethod.Get,
                       $"/api/v1/web-services/{owned.Id}",
                       fixture.Beta,
                       tenantId))
            using (var result = await fixture.Api.SendAsync(rejected, ct))
                Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
            await tenant.SelectOptionAsync(fixture.Beta.DefaultTenantId);
            await Assertions.Expect(tenant).ToHaveValueAsync(fixture.Beta.DefaultTenantId);
            var safeWorkspace = await page.EvaluateAsync<bool>("""
            ([tenant, resource]) => {
              const records = Object.entries(localStorage).filter(([key]) => key.endsWith('.' + tenant));
              return records.length > 0 && records.every(([, value]) => !value.includes(resource));
            }
            """,
                new[] { fixture.Beta.DefaultTenantId, owned.Id });
            Assert.True(safeWorkspace,
                "The selected tenant's persisted workspace must exclude the removed tenant's resource.");

            // Beta has no access to Alpha here. Its account-scoped subscription must still learn
            // that membership was restored; only the subsequent HTTP read can restore write access.
            var hintsBeforeRestore = journal.OwnHints;
            memberId = Text(await ManageMemberAsync(HttpMethod.Post, tenantId, null, TenantRole.Member, ct), "id");
            await WaitUntilAsync(() => journal.OwnHints > hintsBeforeRestore,
                TimeSpan.FromSeconds(15),
                "Restored membership did not reach the own-user subscription without tenant access.",
                ct);
            var restoredHint = journal.LastOwnHint;
            await WaitUntilAsync(() => journal.HasFreshRole(TenantRole.Member, restoredHint),
                TimeSpan.FromSeconds(15),
                "Write access must return through a fresh authoritative membership read.",
                ct);
            await Assertions.Expect(tenant.Locator($"option[value='{tenantId}']")).ToHaveCountAsync(1);
            await tenant.SelectOptionAsync(tenantId);
            await Assertions.Expect(content.GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "New service", Exact = true })).ToBeEnabledAsync();
            await content.GetByRole(AriaRole.Link,
                new LocatorGetByRoleOptions { Name = owned.Name, Exact = true }).ClickAsync();
            await content.GetByRole(AriaRole.Tab,
                new LocatorGetByRoleOptions { Name = "Configuration", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByLabel("Environment variables")).ToBeEnabledAsync();
            Assert.DoesNotContain("REALTIME_MEMBERSHIP_DRAFT",
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
        });
    }

    private async Task LoginAsync(IPage page)
    {
        await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
        await page.GetByLabel("Username or email").FillAsync(fixture.Beta.Email);
        await page.GetByRole(AriaRole.Textbox,
            new PageGetByRoleOptions { Name = "Password", Exact = true }).FillAsync(fixture.Beta.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
        await page.GetByLabel("Tenant").SelectOptionAsync(fixture.Alpha.DefaultTenantId);
    }

    private async Task<JsonElement> ManageMemberAsync(HttpMethod method,
        string tenantId,
        string? memberId,
        TenantRole role,
        CancellationToken ct) => await SendAsync(method,
        $"/api/v1/tenants/{tenantId}/members" + (memberId is null ? "" : $"/{memberId}"),
        fixture.Alpha,
        null,
        method == HttpMethod.Post
            ? new
            {
                userId = fixture.Beta.KeycloakUserId,
                email = fixture.Beta.Email,
                role
            }
            : new
            {
                role
            },
        ct);

    private Task DeleteMemberAsync(string tenantId, string memberId, CancellationToken ct) =>
        DeleteAsync($"/api/v1/tenants/{tenantId}/members/{memberId}", null, ct);

    private async Task<OwnedService> CreateOwnedServiceAsync(TestResourceScope resources,
        string tenantId,
        CancellationToken ct)
    {
        var name = $"rt-member-{Guid.NewGuid():N}"[..20];
        var group = await SendAsync(HttpMethod.Post,
            "/api/v1/resource-groups",
            fixture.Alpha,
            tenantId,
            new
            {
                name,
                description = "Owned realtime membership acceptance",
                tags = new[] { "e2e" }
            },
            ct);
        var groupId = Text(group, "id");
        resources.Defer("delete realtime membership group",
            async cleanup =>
            {
                await DeleteAsync($"/api/v1/resource-groups/{groupId}", tenantId, cleanup);
                await fixture.WaitForNamespaceDeletionAsync(groupId, cleanup);
            });
        var targets = await SendAsync(HttpMethod.Get,
            "/api/v1/deployment-targets/available?resourceType=pole%2Fweb-service&requiresPublicIngress=false",
            fixture.Alpha,
            tenantId,
            null,
            ct);
        var targetId = Text(targets.EnumerateArray().First(), "id");
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
        var created = await SendAsync(HttpMethod.Post,
            "/api/v1/web-services",
            fixture.Alpha,
            tenantId,
            new
            {
                name,
                resourceGroupId = groupId,
                exposure = "Private",
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
                    targets = new[] { new { deploymentTargetId = targetId, fixedReplicas = 1 } }
                }
            },
            ct);
        var id = Text(created, "webServiceId");
        resources.Defer("delete realtime membership service",
            async cleanup =>
            {
                await DeleteAsync($"/api/v1/web-services/{id}", tenantId, cleanup);
                await WaitUntilAsync(async probeCancellation =>
                    {
                        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get,
                            $"/api/v1/web-services/{id}",
                            fixture.Alpha,
                            tenantId);
                        using var response = await fixture.Api.SendAsync(request, probeCancellation);
                        return TestDeletion.IsComplete(response.StatusCode);
                    },
                    TimeSpan.FromMinutes(3),
                    "The owned membership service did not finish cleanup.",
                    cleanup);
            });
        return new OwnedService(id, groupId, name);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method,
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
        Assert.True(response.IsSuccessStatusCode,
            $"Realtime membership API {method} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task DeleteAsync(string path, string? tenantId, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, fixture.Alpha, tenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound,
            $"Realtime membership cleanup returned HTTP {(int)response.StatusCode}.");
    }

    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;

    private static bool HasRole(JsonElement tenants, string tenantId, TenantRole role) =>
        tenants.EnumerateArray().Any(tenant => Text(tenant, "id") == tenantId &&
                                               (tenant.GetProperty("role").ValueKind == JsonValueKind.Number
                                                   ? tenant.GetProperty("role").GetInt32() == (int)role
                                                   : Text(tenant, "role") == role.ToString()));

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message, CancellationToken ct) =>
        TestWait.UntilAsync(_ => Task.FromResult(condition()), timeout, message, ct);

    private static Task WaitUntilAsync(Func<CancellationToken, Task<bool>> condition,
        TimeSpan timeout,
        string message,
        CancellationToken ct) => TestWait.UntilAsync(condition, timeout, message, ct);

    private sealed record OwnedService(
        string Id,
        string GroupId,
        string Name);

    private sealed record MembershipRead(
        long StartedAt,
        JsonElement Tenants);

    private sealed class MembershipJournal
    {
        private readonly string subject;
        private readonly string tenantId;
        private readonly ConcurrentDictionary<IRequest, (long StartedAt, int Document)> discoveryStarts = new();
        private readonly HttpReadJournal<IRequest> discoveryReads = new();
        private readonly ConcurrentQueue<MembershipRead> membershipReads = new();
        private readonly RealtimeCoverageJournal coverage = new();
        private readonly ConcurrentQueue<Task> pending = new();
        private int connections;
        private int documentNavigations;
        private int refreshGrants;
        private int ownHints;
        private int sessionRevocations;
        private int foreignUserHints;
        private int parseFailures;
        private int tenantHeadersOnDiscovery;
        private long lastOwnHint;

        public MembershipJournal(IPage page, string subject, string tenantId)
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
                    new Uri(request.Url).AbsolutePath.EndsWith("/protocol/openid-connect/token",
                        StringComparison.Ordinal) &&
                    request.PostData?.Contains("grant_type=refresh_token", StringComparison.Ordinal) == true)
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
                socket.FrameSent += (_, frame) => ReadFrames(frame.Text,
                    message => coverage.Sent(connection, message));
                socket.FrameReceived += (_, frame) => ReadFrames(frame.Text,
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
        public int DiscoveryReadFailures => discoveryReads.Snapshot().Count(value =>
            value.Outcome is HttpReadOutcome.Rejected or HttpReadOutcome.TransportFailed);
        public int TenantHeadersOnDiscovery => Volatile.Read(ref tenantHeadersOnDiscovery);

        public bool HasOwnCoverage(long after = 0) =>
            coverage.LatestCoveredConnection(values => values.Any(value => value.Covered &&
                value.Topic == "tenant-access" && value.ResourceId == subject && value.TenantId is null), after) > 0;

        public bool HasFreshRole(TenantRole role, long after) => membershipReads.Any(value =>
            value.StartedAt > after && HasRole(value.Tenants, tenantId, role));

        public bool HasFreshAbsence(long after) => membershipReads.Any(value => value.StartedAt > after &&
            value.Tenants.EnumerateArray().All(tenant => Text(tenant, "id") != tenantId));

        public Task DrainAsync() => Task.WhenAll(pending.ToArray());

        private void FinishRead(IRequest request) => discoveryStarts.TryRemove(request, out _);

        private async Task RecordReadAsync(IResponse response, long started, int requestDocument)
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
            {
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
                            "observationVersion"
                        };
                        if (hint.EnumerateObject().Any(property => !allowed.Contains(property.Name)) ||
                            hint.GetProperty("version").GetInt32() != 1 ||
                            hint.TryGetProperty("tenantId", out var tenant) && tenant.ValueKind != JsonValueKind.Null)
                            Interlocked.Increment(ref parseFailures);
                        if (Text(hint, "resourceId") != subject)
                            Interlocked.Increment(ref foreignUserHints);
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
