using System.Collections.Concurrent;
using System.Diagnostics;
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
public sealed class RealtimeObservationBrowserE2ETests(
    PoleE2EFixture fixture,
    ITestOutputHelper output)
{
    private readonly TestTimings _timings = new(typeof(RealtimeObservationBrowserE2ETests));

    [Fact]
    public async Task Owned_native_pod_deletions_reach_current_visible_diagnostics_within_observation_budget()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var testEvidence = new TestPhaseJournal(nameof(Owned_native_pod_deletions_reach_current_visible_diagnostics_within_observation_budget)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = budget.Token;
        var resources = new TestResourceScope("realtime observation browser E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            var owned = await CreateOwnedServiceAsync(resources, ct);
            using (_timings.Measure("e2e.namespace.ready"))
                await fixture.WaitForNamespaceCreationAsync(owned.GroupId, ct);
            using (_timings.Measure("e2e.web-service.ready"))
                await fixture.WaitForWebServiceReadyAsync(owned.GroupId, owned.Name, ct);
            await fixture.UseInteractiveClientTokenLifespanAsync(resources, TimeSpan.FromMinutes(5), ct);
            var service = await SendAsync(HttpMethod.Get, $"/api/v1/web-services/{owned.Id}", null, ct);
            var placement = Assert.Single(service.GetProperty("deployments").EnumerateArray());
            var deploymentId = Text(placement, "deploymentId");
            var revision = placement.GetProperty("deploymentRevision").GetInt64();
            var generation = placement.GetProperty("desiredGeneration").GetInt64();

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
            var journal = new ObservationJournal(page,
                fixture.Alpha.DefaultTenantId,
                owned.Id,
                Text(placement, "deploymentTargetId"),
                deploymentId,
                revision,
                generation);
            await LoginAsync(page);
            await page.GotoAsync(new Uri(fixture.FrontendBaseAddress,
                    $"?view=web-services&resource={owned.Id}&section=operations&tenant={fixture.Alpha.DefaultTenantId}")
                .ToString());
            var row = page.GetByTestId($"operation-row-{deploymentId}");
            await Assertions.Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
            await Assertions.Expect(row.GetByTestId("readiness-status")).ToHaveTextAsync("Ready",
                new LocatorAssertionsToHaveTextOptions { Timeout = 30_000 });
            await row.GetByRole(AriaRole.Button,
                new LocatorGetByRoleOptions { Name = "Runtime logs and events", Exact = true }).ClickAsync();
            var viewer = page.GetByTestId($"runtime-log-viewer-{deploymentId}");
            var instance = viewer.GetByLabel("Instance", new LocatorGetByLabelOptions { Exact = true });
            await WaitUntilAsync(() => journal.HasCurrentCoverage && journal.DiagnosticsVersion > 0,
                TimeSpan.FromSeconds(30),
                "Fresh native observation coverage was not acknowledged.",
                ct);

            const int sampleCount = 20;
            var samples = new List<double>(sampleCount);
            var removed = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < sampleCount; index++)
            {
                using var sampleTiming = _timings.Measure("e2e.observation.sample");
                using var readinessTiming = _timings.Measure("e2e.observation.replacement.ready");
                string? podUid = null;
                // Native readiness and replacement startup are prerequisites, outside the
                // measured deletion-to-visible interval. Every helper revalidates ownership.
                await WaitUntilAsync(async probeCancellation =>
                    {
                        var pods = await fixture.ListOwnedObservationPodsAsync(fixture.Alpha,
                            fixture.Alpha.DefaultTenantId,
                            owned.Id,
                            deploymentId,
                            probeCancellation);
                        var ready = pods.SingleOrDefault(value => value.Ready && !removed.Contains(value.Uid));
                        if (ready is null || !journal.HasCurrentCoverage ||
                            !await viewer.IsVisibleAsync().WaitAsync(probeCancellation) ||
                            await instance.Locator($"option[value='{ready.Uid}']").CountAsync().WaitAsync(probeCancellation) != 1)
                            return false;
                        podUid = ready.Uid;
                        return true;
                    },
                    TimeSpan.FromMinutes(2),
                    "A fresh owned replacement was not ready and visible before the next sample.",
                    ct);
                Assert.NotNull(podUid);
                readinessTiming.Dispose();
                await instance.SelectOptionAsync(podUid);
                var before = Math.Max(journal.HintVersion, journal.DiagnosticsVersion);
                Assert.True(journal.HasCurrentCoverage && before > 0,
                    "Each sample must start with current native coverage and a positive retained version.");

                // Starts before the real Kubernetes delete, including the helper's final UID
                // and placement verification. This is an upper bound, not an exact commit time.
                var started = Stopwatch.GetTimestamp();
                await fixture.DeleteOwnedObservationPodAsync(fixture.Alpha,
                    fixture.Alpha.DefaultTenantId,
                    owned.Id,
                    deploymentId,
                    podUid,
                    ct);
                await WaitUntilAsync(async probeCancellation => journal.HasCurrentCoverage && journal.HintVersion > before &&
                                                 journal.CurrentDiagnosticsExclude(podUid) &&
                                                 await viewer.IsVisibleAsync().WaitAsync(probeCancellation) &&
                                                 await instance.Locator($"option[value='{podUid}']").CountAsync().WaitAsync(probeCancellation) == 0,
                    TimeSpan.FromSeconds(15),
                    "The committed native deletion did not reach current visible instance metadata.",
                    ct);
                samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (await instance.CountAsync() > 0)
                    Assert.Equal(string.Empty, await instance.InputValueAsync());
                removed.Add(podUid);
            }

            await journal.DrainAsync();
            Assert.Equal(0, journal.ParseFailures);
            Assert.Equal(0, journal.DiagnosticReadFailures);
            Assert.Equal(sampleCount, removed.Count);
            var sorted = samples.Order().ToArray();
            var p95 = sorted[(int)Math.Ceiling(sampleCount * 0.95) - 1];
            output.WriteLine(
                $"Native WebService observation: {sampleCount} owned Pod UID-precondition deletes; write-start→visible old UID removal upper bound p95={p95:F0}ms, max={sorted[^1]:F0}ms.");
            output.WriteLine(
                "Replacement readiness is awaited outside each sample; this measures native deletion visibility, not rollout or replacement startup latency.");
            Assert.True(p95 <= 5_000,
                "The conservative native write-start to visible UID removal p95 must be at most five seconds.");
        });
    }

    private async Task<OwnedService> CreateOwnedServiceAsync(TestResourceScope resources, CancellationToken ct)
    {
        var name = $"observation-{Guid.NewGuid():N}"[..20];
        var group = await SendAsync(HttpMethod.Post,
            "/api/v1/resource-groups",
            new
            {
                name,
                description = "Owned native observation browser acceptance",
                tags = new[] { "e2e" }
            },
            ct);
        var groupId = Text(group, "id");
        resources.Defer("delete observation group and owned namespace", cleanup => DeleteGroupAsync(groupId, cleanup));
        var targets = await SendAsync(HttpMethod.Get,
            "/api/v1/deployment-targets/available?resourceType=pole%2Fweb-service&requiresPublicIngress=false",
            null,
            ct);
        var targetId = Text(targets.EnumerateArray().First(), "id");
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
        var created = await SendAsync(HttpMethod.Post,
            "/api/v1/web-services",
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
        resources.Defer("delete observation service", cleanup => DeleteServiceAsync(id, cleanup));
        return new OwnedService(id, groupId, name);
    }

    private async Task LoginAsync(IPage page)
    {
        await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
        await page.GetByLabel("Username or email").FillAsync(fixture.Alpha.Email);
        await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true })
            .FillAsync(fixture.Alpha.Password);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
        await page.GetByLabel("Tenant").SelectOptionAsync(fixture.Alpha.DefaultTenantId);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request =
            fixture.CreateAuthenticatedRequest(method, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode,
            $"Observation browser API {method} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    private async Task DeleteServiceAsync(string id, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteServiceAsync");
        await DeleteAsync($"/api/v1/web-services/{id}", ct);
        await WaitUntilAsync(async probeCancellation =>
            {
                using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get,
                    $"/api/v1/web-services/{id}",
                    fixture.Alpha,
                    fixture.Alpha.DefaultTenantId);
                using var response = await fixture.Api.SendAsync(request, probeCancellation);
                return TestDeletion.IsComplete(response.StatusCode);
            },
            TimeSpan.FromMinutes(3),
            "The owned observation service did not finish cleanup.",
            ct);
    }

    private async Task DeleteGroupAsync(string id, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteGroupAsync");
        await DeleteAsync($"/api/v1/resource-groups/{id}", ct);
        await fixture.WaitForNamespaceDeletionAsync(id, ct);
    }

    private async Task DeleteAsync(string path, CancellationToken ct)
    {
        using var timing = _timings.Measure("e2e.cleanup.DeleteAsync");
        using var request =
            fixture.CreateAuthenticatedRequest(HttpMethod.Delete, path, fixture.Alpha, fixture.Alpha.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound,
            $"Observation browser cleanup returned HTTP {(int)response.StatusCode}.");
    }

    private static string Text(JsonElement value, string property) => value.GetProperty(property).GetString()!;

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message, CancellationToken ct) =>
        TestWait.UntilAsync(_ => Task.FromResult(condition()), timeout, message, ct, TimeSpan.FromMilliseconds(50));

    private static Task WaitUntilAsync(Func<CancellationToken, Task<bool>> condition,
        TimeSpan timeout,
        string message,
        CancellationToken ct) => TestWait.UntilAsync(condition, timeout, message, ct, TimeSpan.FromMilliseconds(50));

    private sealed record OwnedService(
        string Id,
        string GroupId,
        string Name);

    private sealed record DiagnosticsMetadata(
        long Version,
        bool Available,
        IReadOnlySet<string> InstanceUids);

    private sealed class ObservationJournal
    {
        private readonly RealtimeCoverageJournal coverage = new();
        private readonly HttpReadJournal<IRequest> reads = new();
        private readonly ConcurrentDictionary<IRequest, int> diagnosticRequests = new();
        private readonly ConcurrentQueue<Task> pending = new();
        private int connections;
        private int documentEpoch;
        private int parseFailures;
        private long hintVersion;
        private DiagnosticsMetadata? diagnostics;
        public int ParseFailures => Volatile.Read(ref parseFailures);
        public int DiagnosticReadFailures => reads.Snapshot().Count(value =>
            value.Outcome is HttpReadOutcome.Rejected or HttpReadOutcome.TransportFailed);
        public long HintVersion => Interlocked.Read(ref hintVersion);
        public long DiagnosticsVersion => Volatile.Read(ref diagnostics)?.Version ?? 0;

        public bool CurrentDiagnosticsExclude(string uid) =>
            Volatile.Read(ref diagnostics) is { Available: true } value &&
            value.Version >= HintVersion && !value.InstanceUids.Contains(uid);

        public bool HasCurrentCoverage => coverage.LatestCoveredConnection(values =>
            values.Any(value => value.Covered && value.Topic == "runtime-observations" &&
                value.TenantId == tenantId && value.ResourceId == deploymentId)) > 0;

        private readonly string tenantId;
        private readonly string serviceId;
        private readonly string targetId;
        private readonly string deploymentId;
        private readonly long revision;
        private readonly long generation;

        public ObservationJournal(IPage page,
            string tenantId,
            string serviceId,
            string targetId,
            string deploymentId,
            long revision,
            long generation)
        {
            this.tenantId = tenantId;
            this.serviceId = serviceId;
            this.targetId = targetId;
            this.deploymentId = deploymentId;
            this.revision = revision;
            this.generation = generation;
            page.Request += (_, request) =>
            {
                if (request.IsNavigationRequest && request.Frame == page.MainFrame)
                {
                    Interlocked.Increment(ref documentEpoch);
                    coverage.Navigate();
                    reads.Navigate();
                    Interlocked.Exchange(ref hintVersion, 0);
                    Interlocked.Exchange(ref diagnostics, null);
                }

                if (request.Method == "GET" && new Uri(request.Url).AbsolutePath ==
                    $"/api/v1/resource-deployments/{deploymentId}/diagnostics")
                {
                    diagnosticRequests[request] = Volatile.Read(ref documentEpoch);
                    reads.Start(request, new Uri(request.Url).AbsolutePath, Stopwatch.GetTimestamp());
                }
            };
            page.RequestFinished += (_sender, request) =>
            {
                reads.Finish(request);
                diagnosticRequests.TryRemove(request, out _);
            };
            page.RequestFailed += (_sender, request) =>
            {
                reads.Finish(request, failed: true, cancelled: request.Failure == "net::ERR_ABORTED");
                diagnosticRequests.TryRemove(request, out _);
            };
            page.Response += (_, response) =>
            {
                reads.Response(response.Request, response.Status);
                if (response.Ok && response.Request.Method == "GET" &&
                    new Uri(response.Url).AbsolutePath == $"/api/v1/resource-deployments/{deploymentId}/diagnostics" &&
                    diagnosticRequests.TryRemove(response.Request, out var requestDocument))
                    pending.Enqueue(RecordDiagnosticsAsync(response, requestDocument));
            };
            page.WebSocket += (_pageSender, socket) =>
            {
                if (new Uri(socket.Url).AbsolutePath != "/api/v1/realtime/hub")
                    return;
                var connection = Interlocked.Increment(ref connections);
                coverage.Open(connection, Stopwatch.GetTimestamp());
                socket.Close += (_, _) => coverage.Close(connection);
                socket.FrameSent += (_, frame) => ParseFrames(frame.Text,
                    message => coverage.Sent(connection, message));
                socket.FrameReceived += (_, frame) => ParseFrames(frame.Text,
                    message =>
                    {
                        if (!coverage.IsCurrent(connection))
                            return;
                        coverage.Received(connection, message, Stopwatch.GetTimestamp());
                        if (message.TryGetProperty("target", out var target) && target.GetString() == "Changed")
                            RecordHint(message.GetProperty("arguments")[0]);
                    });
            };
        }

        private async Task RecordDiagnosticsAsync(IResponse response, int requestDocument)
        {
            try
            {
                var value = await response.JsonAsync();
                if (requestDocument != Volatile.Read(ref documentEpoch))
                    return;
                if (value is not { } snapshot || OptionalText(snapshot, "deploymentId") != deploymentId ||
                    OptionalText(snapshot, "resourceType") != "pole/web-service" ||
                    OptionalText(snapshot, "resourceId") != serviceId ||
                    OptionalText(snapshot, "targetId") != targetId ||
                    Number(snapshot, "deploymentRevision") != revision ||
                    Number(snapshot, "desiredGeneration") != generation ||
                    !snapshot.TryGetProperty("checkedAtUtc", out var checkedAt) ||
                    !checkedAt.TryGetDateTimeOffset(out _) || !snapshot.TryGetProperty("diagnostics", out var detail) ||
                    OptionalText(detail, "kind") is not ("Available" or "Unavailable"))
                {
                    Interlocked.Increment(ref parseFailures);
                    return;
                }

                var available = OptionalText(detail, "kind") == "Available";
                if (available && (!detail.TryGetProperty("instances", out var instances) ||
                                  instances.ValueKind != JsonValueKind.Array ||
                                  instances.EnumerateArray()
                                      .Any(item => string.IsNullOrEmpty(OptionalText(item, "uid")))))
                {
                    Interlocked.Increment(ref parseFailures);
                    return;
                }

                // Cold and live fallback responses can legitimately precede the first
                // positive retained control version. They cannot prove this benchmark.
                if (!snapshot.TryGetProperty("observationVersion", out var observationVersion) ||
                    observationVersion.ValueKind == JsonValueKind.Null)
                    return;
                if (observationVersion.ValueKind != JsonValueKind.Number ||
                    !observationVersion.TryGetInt64(out var version) || version < 0)
                {
                    Interlocked.Increment(ref parseFailures);
                    return;
                }

                if (version == 0)
                    return;
                var instanceUids = available
                    ? detail.GetProperty("instances").EnumerateArray()
                        .Select(item => Text(item, "uid")).ToHashSet(StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                var next = new DiagnosticsMetadata(version, available, instanceUids);
                DiagnosticsMetadata? current;
                do
                {
                    if (requestDocument != Volatile.Read(ref documentEpoch))
                        return;
                    current = Volatile.Read(ref diagnostics);
                    if (current?.Version > version)
                        return;
                } while (Interlocked.CompareExchange(ref diagnostics, next, current) != current);
            }
            catch (Exception failure) when (failure is PlaywrightException or JsonException or InvalidOperationException
                                                or FormatException or KeyNotFoundException)
            {
                Interlocked.Increment(ref parseFailures);
            }
        }

        private void RecordHint(JsonElement hint)
        {
            var allowed = new[]
            {
                "version", "topic", "tenantId", "resourceId", "resourceGroupId", "revision", "generation",
                "observationVersion"
            };
            if (hint.EnumerateObject().Any(property => !allowed.Contains(property.Name)) ||
                Number(hint, "version") != 1)
            {
                Interlocked.Increment(ref parseFailures);
                return;
            }

            if (OptionalText(hint, "topic") == "runtime-observations" && OptionalText(hint, "tenantId") == tenantId &&
                OptionalText(hint, "resourceId") == deploymentId && Number(hint, "revision") == revision &&
                Number(hint, "generation") == generation && Number(hint, "observationVersion") is long version &&
                version > 0)
                Advance(ref hintVersion, version);
        }

        private void ParseFrames(string? text, Action<JsonElement> consume)
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
                    consume(document.RootElement);
                }
                catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException
                                                    or KeyNotFoundException)
                {
                    Interlocked.Increment(ref parseFailures);
                }
            }
        }

        private static long? Number(JsonElement value, string property) =>
            value.TryGetProperty(property, out var number) && number.ValueKind == JsonValueKind.Number
                ? number.GetInt64()
                : null;

        private static string? OptionalText(JsonElement value, string property) =>
            value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;

        private static void Advance(ref long field, long next)
        {
            long current;
            do
            {
                current = Interlocked.Read(ref field);
                if (current >= next)
                    return;
            } while (Interlocked.CompareExchange(ref field, next, current) != current);
        }

        public Task DrainAsync() => Task.WhenAll(pending.ToArray());
    }
}
