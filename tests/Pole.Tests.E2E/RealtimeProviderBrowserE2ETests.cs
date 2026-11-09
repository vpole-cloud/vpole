using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class RealtimeProviderBrowserE2ETests(PoleE2EFixture fixture)
{
    private readonly TestTimings _timings = new(typeof(RealtimeProviderBrowserE2ETests));

    [Fact]
    public async Task Native_storage_hints_preserve_dirty_draft_coalesce_slow_reads_and_keep_quiet_inventory_current()
    {
        using var caseTiming = _timings.Measure("e2e.case");
        using var setupTiming = _timings.Measure("e2e.setup");
        using var testEvidence = new TestPhaseJournal(nameof(Native_storage_hints_preserve_dirty_draft_coalesce_slow_reads_and_keep_quiet_inventory_current)).Activate();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = budget.Token;
        var resources = new TestResourceScope("storage observation browser E2E", timings: _timings);
        await resources.RunAsync(async () =>
        {
            using var request = fixture.CreatePlatformAdminRequest(HttpMethod.Get, "/api/v1/platform/deployment-targets");
            using var response = await fixture.Api.SendAsync(request, ct);
            Assert.True(response.IsSuccessStatusCode, "The administrator deployment-target read failed.");
            var targets = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var target = targets.EnumerateArray().Single(item =>
                item.GetProperty("isDefault").GetBoolean() && item.GetProperty("enabled").GetBoolean());
            var targetId = target.GetProperty("id").GetString()!;
            var connectionVersion = target.GetProperty("connection").GetProperty("version").GetInt64();
            var canary = $"native-sensitive-{Guid.NewGuid():N}";

            string browserEndpoint;
            using (_timings.Measure("e2e.browser.acquire"))
                browserEndpoint = await fixture.GetBrowserEndpointAsync(ct);
            using var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.ConnectAsync(browserEndpoint,
                new BrowserTypeConnectOptions { ExposeNetwork = "<loopback>" });
            await using var browserDisposal = _timings.MeasureDisposal(browser, "e2e.browser.dispose");
            setupTiming.Dispose();
            using var scenarioTiming = _timings.Measure("e2e.scenario");
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                IgnoreHTTPSErrors = true
            });
            await using var contextDisposal = _timings.MeasureDisposal(context, "e2e.browser.context.dispose");
            var page = await BrowserEvidence.NewPageAsync(context);
            var journal = new StorageJournal(page, targetId, connectionVersion, canary);
            await page.GotoAsync(fixture.FrontendBaseAddress.ToString());
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Log in", Exact = true }).ClickAsync();
            await page.GetByLabel("Username or email").FillAsync(fixture.PlatformAdminUser.Email);
            await page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions { Name = "Password", Exact = true })
                .FillAsync(fixture.PlatformAdminUser.Password);
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign In", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Storage", Exact = true }).ClickAsync();
            var displayName = page.GetByLabel("Display name", new PageGetByLabelOptions { Exact = true });
            var selectedTarget = page.GetByLabel("Deployment target", new PageGetByLabelOptions { Exact = true });
            var create = page.GetByRole(AriaRole.Button,
                new PageGetByRoleOptions { Name = "Create profile", Exact = true });
            await Assertions.Expect(displayName)
                .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
            await Assertions.Expect(selectedTarget).ToHaveValueAsync(targetId);
            await WaitUntilAsync(() =>
                    journal.Covered && journal.Latest is { Version: > 0 } && journal.ReadsQuiet(TimeSpan.FromSeconds(1)),
                TimeSpan.FromSeconds(30),
                "A complete native storage subscription and initial coherent read were not acknowledged.",
                ct);
            const string draft = "Retained native storage draft";
            await displayName.FillAsync(draft);
            await Assertions.Expect(create).ToBeEnabledAsync();
            var originalInput = await displayName.ElementHandleAsync() ??
                                throw new InvalidOperationException("The visible storage input was absent.");
            // Semantic heading anchors the native HTML section; no CSS class or frontend test ID is required.
            var inventory = page
                .GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = "Storage classes", Exact = true })
                .Locator("xpath=ancestor::section[1]");
            var documentNavigations = journal.DocumentNavigations;
            var baselineHint = journal.HintVersion;
            var captured = new TaskCompletionSource<CapturedRead>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IRequest? heldRequest = null;
            var intercepted = 0;
            const string observationRoute = "**/api/v1/platform/storage/observation?*";
            await page.RouteAsync(observationRoute,
                async route =>
                {
                    if (!journal.IsObservation(route.Request) || journal.HintVersion <= baselineHint ||
                        Interlocked.CompareExchange(ref intercepted, 1, 0) != 0)
                    {
                        await route.ContinueAsync();
                        return;
                    }

                    heldRequest = route.Request;
                    try
                    {
                        var fetched = await route.FetchAsync();
                        try
                        {
                            Assert.True(fetched.Ok,
                                "The delayed coherent inventory must be a successful real API response.");
                            using var document = JsonDocument.Parse(await fetched.TextAsync());
                            var snapshot = journal.Metadata(document.RootElement);
                            Assert.True(snapshot.Version > 0,
                                "The delayed coherent snapshot must have an actual retained observation version.");
                            captured.TrySetResult(new CapturedRead(route.Request, Stopwatch.GetTimestamp(), snapshot));
                            await release.Task.WaitAsync(ct);
                            await route.FulfillAsync(new RouteFulfillOptions { Response = fetched });
                            delivered.TrySetResult();
                        }
                        finally
                        {
                            await fetched.DisposeAsync();
                        }
                    }
                    catch (PlaywrightException)
                    {
                        // Transport diagnostics may contain credentials. Keep a failing fixed message.
                        var failure = new InvalidOperationException("The controlled authenticated storage read failed.");
                        captured.TrySetException(failure);
                        delivered.TrySetException(failure);
                    }
                    catch (Exception failure)
                    {
                        captured.TrySetException(failure);
                        delivered.TrySetException(failure);
                    }
                });
            try
            {
                var firstName = await fixture.CreateOwnedStorageObservationClassAsync(resources, targetId, canary, ct);
                var held = await captured.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                Assert.Same(heldRequest, held.Request);
                Assert.Contains(firstName, held.Snapshot.ClassNames);
                var secondName = await fixture.CreateOwnedStorageObservationClassAsync(resources, targetId, canary, ct);
                await WaitUntilAsync(() => journal.HintVersion > held.Snapshot.Version,
                    TimeSpan.FromSeconds(15),
                    "The second native class change did not deliver a newer observation hint.",
                    ct);
                Assert.DoesNotContain(secondName, held.Snapshot.ClassNames);
                // Record every DOM commit: accepting the held old snapshot would briefly show only
                // the first class. A trailing authoritative read adds both classes in one render.
                await inventory.EvaluateAsync("""
                (section, names) => {
                  const state = { stale: false, observer: null };
                  state.observer = new MutationObserver(() => {
                    const text = section.textContent ?? '';
                    if (text.includes(names[0]) && !text.includes(names[1])) state.stale = true;
                  });
                  state.observer.observe(section, { childList: true, subtree: true, characterData: true });
                  section.__poleStorageRegression = state;
                }
                """,
                    new[] { firstName, secondName });
                var afterSecondHint = journal.HintVersion;
                var remaining = TimeSpan.FromSeconds(35) - Stopwatch.GetElapsedTime(held.CapturedAt);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, ct);
                Assert.True(journal.Covered, "Native coverage must remain acknowledged during the slow inventory read.");
                Assert.True(journal.HintVersion > afterSecondHint,
                    "Unchanged native inventory must still deliver its thirty-second validation hint during the held read.");
                Assert.False(journal.Failed(held.Request),
                    "Hints and the covered polling interval must not abort the slow HTTP read.");
                Assert.Equal(1, journal.ReadsSince(held.Request));
                release.TrySetResult();
                await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                await WaitUntilAsync(() => journal.Finished(held.Request) && journal.Latest is { } current &&
                                           current.Version >= journal.HintVersion &&
                                           current.Version > held.Snapshot.Version &&
                                           current.ClassNames.Contains(firstName) &&
                                           current.ClassNames.Contains(secondName) &&
                                           journal.ReadsQuiet(TimeSpan.FromMilliseconds(500)),
                    TimeSpan.FromSeconds(15),
                    "One coalesced trailing read did not replace the overtaken coherent snapshot.",
                    ct);
                Assert.Equal(2, journal.ReadsSince(held.Request));
                Assert.False(journal.Failed(held.Request));
                await Assertions.Expect(inventory.GetByText(firstName, new LocatorGetByTextOptions { Exact = true }))
                    .ToBeVisibleAsync();
                await Assertions.Expect(inventory.GetByText(secondName, new LocatorGetByTextOptions { Exact = true }))
                    .ToBeVisibleAsync();
                Assert.True(await inventory.EvaluateAsync<bool>("section => !section.__poleStorageRegression.stale"),
                    "An overtaken response must never render inventory below the latest observation floor.");
                await AssertDraftAsync();

                var quietBaseline = journal.Latest!;
                // Stay beyond the forty-five-second freshness window without another native write
                // or a manual refresh. Real periodic validation must keep the accepted UI writable.
                await Task.Delay(TimeSpan.FromSeconds(46), ct);
                await WaitUntilAsync(async probeCancellation => journal.Covered && journal.Latest is { } current &&
                                                 current.Version > quietBaseline.Version &&
                                                 current.CheckedAt > quietBaseline.CheckedAt &&
                                                 await create.IsEnabledAsync().WaitAsync(probeCancellation),
                    TimeSpan.FromSeconds(15),
                    "Healthy unchanged native inventory became stale instead of accepting periodic validation.",
                    ct);
                await AssertDraftAsync();
                Assert.Equal(documentNavigations, journal.DocumentNavigations);
                await journal.DrainAsync();
                Assert.Equal(0, journal.ParseFailures);
                Assert.Equal(0, journal.ObservationReadFailures);
                Assert.True(journal.TransportSafe,
                    "Realtime transport URLs and hub authentication headers must not expose bearer credentials.");
                await AssertNoReadableCredentialsAsync(page);
            }
            finally
            {
                release.TrySetResult();
                await page.UnrouteAsync(observationRoute);
                await inventory.EvaluateAsync("section => section.__poleStorageRegression?.observer?.disconnect()");
                await originalInput.DisposeAsync();
            }

            async Task AssertDraftAsync()
            {
                await Assertions.Expect(displayName).ToHaveValueAsync(draft);
                await Assertions.Expect(selectedTarget).ToHaveValueAsync(targetId);
                await Assertions.Expect(create).ToBeEnabledAsync();
                Assert.True(await originalInput.EvaluateAsync<bool>("input => input.isConnected"),
                    "Native inventory updates must not remount the dirty form.");
            }
        });
    }

    private static async Task AssertNoReadableCredentialsAsync(IPage page)
    {
        // Only a boolean leaves JavaScript; cookie Path is checked without navigation or reload.
        Assert.True(await page.EvaluateAsync<bool>("""
            () => {
              const jwt = /eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}/;
              const cookie = /PoleRealtime\.Dev|__Secure-PoleRealtime/;
              const values = [location.href, document.cookie];
              const url = location.href, state = history.state;
              try { history.replaceState(state, '', '/api/v1/realtime/session'); values.push(document.cookie); }
              finally { history.replaceState(state, '', url); }
              for (const storage of [localStorage, sessionStorage])
                for (let i = 0; i < storage.length; i++) { const key = storage.key(i); values.push(key, storage.getItem(key) ?? ''); }
              return values.every(value => !jwt.test(value) && !cookie.test(value));
            }
            """),
            "JWT and realtime cookies must remain absent from readable cookies, URLs and browser storage.");
    }

    private static Task WaitUntilAsync(Func<bool> ready, TimeSpan timeout, string message, CancellationToken ct) =>
        TestWait.UntilAsync(_ => Task.FromResult(ready()), timeout, message, ct);

    private static Task WaitUntilAsync(Func<CancellationToken, Task<bool>> ready,
        TimeSpan timeout,
        string message,
        CancellationToken ct) => TestWait.UntilAsync(ready, timeout, message, ct);

    private sealed record Snapshot(
        long Version,
        DateTimeOffset CheckedAt,
        HashSet<string> ClassNames);

    private sealed record CapturedRead(
        IRequest Request,
        long CapturedAt,
        Snapshot Snapshot);

    private sealed record Read(
        long StartedAt,
        int Document);

    private sealed class StorageJournal
    {
        private readonly string targetId;
        private readonly long connectionVersion;
        private readonly string canary;
        private readonly ConcurrentDictionary<IRequest, Read> reads = new();
        private readonly ConcurrentDictionary<IRequest, bool> finished = new();
        private readonly ConcurrentDictionary<IRequest, bool> failed = new();
        private readonly RealtimeCoverageJournal coverage = new();
        private readonly HttpReadJournal<IRequest> outcomes = new();
        private readonly ConcurrentQueue<Task> pending = new();

        private static readonly Regex Jwt = new(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",
            RegexOptions.CultureInvariant);

        private int connection;
        private int documentNavigations;
        private int parseFailures;
        private int unsafeTransport;
        private long hintVersion;
        private Snapshot? latest;
        public int DocumentNavigations => Volatile.Read(ref documentNavigations);
        public int ParseFailures => Volatile.Read(ref parseFailures);
        public bool TransportSafe => Volatile.Read(ref unsafeTransport) == 0;
        public bool Covered => coverage.LatestCoveredConnection(values => values.Any(value => value.Covered &&
            value.Topic == "storage-observations" && value.ResourceId == targetId && value.TenantId is null)) > 0;
        public int ObservationReadFailures => outcomes.Snapshot().Count(value =>
            value.Outcome is HttpReadOutcome.Rejected or HttpReadOutcome.TransportFailed);
        public long HintVersion => Interlocked.Read(ref hintVersion);
        public Snapshot? Latest => Volatile.Read(ref latest);

        public StorageJournal(IPage page, string targetId, long connectionVersion, string canary)
        {
            this.targetId = targetId;
            this.connectionVersion = connectionVersion;
            this.canary = canary;
            page.Request += (_, request) =>
            {
                if (request.IsNavigationRequest && request.Frame == page.MainFrame)
                {
                    Interlocked.Increment(ref documentNavigations);
                    coverage.Navigate();
                    outcomes.Navigate();
                    Interlocked.Exchange(ref hintVersion, 0);
                    Interlocked.Exchange(ref latest, null);
                }

                CheckUrl(request.Url);
                var path = new Uri(request.Url).AbsolutePath;
                if ((path == "/api/v1/realtime/session" ||
                     path.StartsWith("/api/v1/realtime/hub", StringComparison.Ordinal)) &&
                    (request.Headers.ContainsKey("x-pole-tenant-id") ||
                     path == "/api/v1/realtime/session" && request.Method == "POST" &&
                     !request.Headers.ContainsKey("authorization") ||
                     path.StartsWith("/api/v1/realtime/hub", StringComparison.Ordinal) &&
                     request.Headers.ContainsKey("authorization")))
                    Interlocked.Increment(ref unsafeTransport);
                if (IsObservation(request))
                {
                    var started = Stopwatch.GetTimestamp();
                    reads[request] = new Read(started, DocumentNavigations);
                    outcomes.Start(request, path, started);
                }
            };
            page.RequestFinished += (_, request) =>
            {
                outcomes.Finish(request);
                if (reads.ContainsKey(request))
                    finished[request] = true;
            };
            page.RequestFailed += (_, request) =>
            {
                outcomes.Finish(request, failed: true, cancelled: request.Failure == "net::ERR_ABORTED");
                if (reads.ContainsKey(request))
                {
                    failed[request] = true;
                    finished[request] = true;
                }
            };
            page.Response += (_, response) =>
            {
                outcomes.Response(response.Request, response.Status);
                if (IsObservation(response.Request) && reads.TryGetValue(response.Request, out var read))
                {
                    if (!response.Ok)
                        failed[response.Request] = true;
                    pending.Enqueue(RecordAsync(response, read.Document));
                }
            };
            page.WebSocket += (_pageSender, socket) =>
            {
                CheckUrl(socket.Url);
                if (new Uri(socket.Url).AbsolutePath != "/api/v1/realtime/hub")
                    return;
                var id = Interlocked.Increment(ref connection);
                coverage.Open(id, Stopwatch.GetTimestamp());
                socket.Close += (_, _) => coverage.Close(id);
                socket.FrameSent += (_, frame) => ParseFrames(frame.Text,
                    message => coverage.Sent(id, message));
                socket.FrameReceived += (_, frame) => ParseFrames(frame.Text,
                    message =>
                    {
                        if (!coverage.IsCurrent(id))
                            return;
                        coverage.Received(id, message, Stopwatch.GetTimestamp());
                        if (Text(message, "target") == "Changed")
                        {
                            var change = message.GetProperty("arguments")[0];
                            if (Text(change, "topic") == "storage-observations" &&
                                Text(change, "resourceId") == targetId)
                            {
                                var fields = new[]
                                {
                                    "version", "topic", "tenantId", "resourceId", "resourceGroupId", "revision",
                                    "generation", "observationVersion"
                                };
                                if (change.EnumerateObject().Any(item => !fields.Contains(item.Name)) ||
                                    Number(change, "version") != 1 ||
                                    Text(change, "tenantId") is not null ||
                                    Text(change, "resourceGroupId") is not null ||
                                    Number(change, "revision") != connectionVersion)
                                    Interlocked.Increment(ref parseFailures);
                                var version = Number(change, "observationVersion");
                                var previous = HintVersion;
                                while (version > previous)
                                {
                                    var current = Interlocked.CompareExchange(ref hintVersion, version, previous);
                                    if (current == previous)
                                        break;
                                    previous = current;
                                }
                            }
                        }
                    });
            };
        }

        public bool IsObservation(IRequest request)
        {
            var uri = new Uri(request.Url);
            return request.Method == "GET" && uri.AbsolutePath == "/api/v1/platform/storage/observation" &&
                   uri.Query.TrimStart('?').Split('&').Any(item => item == $"deploymentTargetId={targetId}");
        }

        public bool Finished(IRequest request) => finished.ContainsKey(request);
        public bool Failed(IRequest request) => failed.ContainsKey(request);

        public int ReadsSince(IRequest request) => reads.TryGetValue(request, out var value)
            ? reads.Values.Count(item => item.StartedAt >= value.StartedAt)
            : 0;

        public bool ReadsQuiet(TimeSpan quiet) => outcomes.Quiet(_ => true, quiet);

        public Snapshot Metadata(JsonElement value)
        {
            Assert.Equal(targetId, Text(value, "deploymentTargetId"));
            Assert.Equal(connectionVersion, Number(value, "connectionVersion"));
            Assert.True(value.GetProperty("complete").GetBoolean());
            Assert.False(value.GetRawText().Contains(canary, StringComparison.Ordinal),
                "Sensitive native fields must not enter coherent discovery.");
            foreach (var field in new[] { "storageClasses", "persistentVolumes", "persistentVolumeClaims" })
                Assert.Equal(JsonValueKind.Array, value.GetProperty(field).ValueKind);
            var version = Number(value, "observationVersion");
            return new Snapshot(version,
                value.GetProperty("checkedAtUtc").GetDateTimeOffset(),
                value.GetProperty("storageClasses").EnumerateArray().Select(item => Text(item, "name")!)
                    .ToHashSet(StringComparer.Ordinal));
        }

        private async Task RecordAsync(IResponse response, int requestDocument)
        {
            try
            {
                Assert.True(response.Ok, "A coherent browser observation request returned failure.");
                if (await response.JsonAsync() is not { } value)
                {
                    Interlocked.Increment(ref parseFailures);
                    return;
                }

                if (requestDocument != DocumentNavigations)
                    return;
                var snapshot = Metadata(value);
                Snapshot? previous;
                do
                {
                    if (requestDocument != DocumentNavigations)
                        return;
                    previous = Latest;
                    if (previous?.Version > snapshot.Version)
                        return;
                } while (Interlocked.CompareExchange(ref latest, snapshot, previous) != previous);
            }
            catch (Exception failure) when (failure is PlaywrightException or JsonException or InvalidOperationException
                                                or KeyNotFoundException)
            {
                Interlocked.Increment(ref parseFailures);
            }
        }

        private void CheckUrl(string url)
        {
            var keys = new Uri(url).Query.TrimStart('?').Split('&')
                .Select(item => Uri.UnescapeDataString(item.Split('=', 2)[0]));
            if (Jwt.IsMatch(Uri.UnescapeDataString(url)) || keys.Any(key =>
                    key is "access_token" or "refresh_token" or "id_token" or "PoleRealtime.Dev"
                        or "__Secure-PoleRealtime"))
                Interlocked.Increment(ref unsafeTransport);
        }

        private void ParseFrames(string? text, Action<JsonElement> read)
        {
            if (text is null)
            {
                Interlocked.Increment(ref parseFailures);
                return;
            }

            foreach (var frame in text.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                try
                {
                    using var value = JsonDocument.Parse(frame);
                    read(value.RootElement);
                }
                catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException
                                                    or KeyNotFoundException)
                {
                    Interlocked.Increment(ref parseFailures);
                }
        }

        public Task DrainAsync() => Task.WhenAll(pending.ToArray());

        private static string? Text(JsonElement value, string key) =>
            value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

        private static long Number(JsonElement value, string key) => value.TryGetProperty(key, out var item) &&
                                                                     item.ValueKind == JsonValueKind.Number &&
                                                                     item.TryGetInt64(out var number)
            ? number
            : 0;
    }
}
