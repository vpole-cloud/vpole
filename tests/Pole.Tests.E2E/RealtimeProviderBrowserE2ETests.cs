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
    [Fact]
    public async Task Native_storage_hints_preserve_dirty_draft_coalesce_slow_reads_and_keep_quiet_inventory_current()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = budget.Token;
        await using var resources = new TestResourceScope("storage observation browser E2E");
        using var request = fixture.CreatePlatformAdminRequest(HttpMethod.Get, "/api/v1/platform/deployment-targets");
        using var response = await fixture.Api.SendAsync(request, ct);
        Assert.True(response.IsSuccessStatusCode, "The administrator deployment-target read failed.");
        var targets = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var target = targets.EnumerateArray().Single(item =>
            item.GetProperty("isDefault").GetBoolean() && item.GetProperty("enabled").GetBoolean());
        var targetId = target.GetProperty("id").GetString()!;
        var connectionVersion = target.GetProperty("connection").GetProperty("version").GetInt64();
        var canary = $"native-sensitive-{Guid.NewGuid():N}";

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
            await WaitUntilAsync(async () => journal.Covered && journal.Latest is { } current &&
                                             current.Version > quietBaseline.Version &&
                                             current.CheckedAt > quietBaseline.CheckedAt &&
                                             await create.IsEnabledAsync(),
                TimeSpan.FromSeconds(15),
                "Healthy unchanged native inventory became stale instead of accepting periodic validation.",
                ct);
            await AssertDraftAsync();
            Assert.Equal(documentNavigations, journal.DocumentNavigations);
            await journal.DrainAsync();
            Assert.Equal(0, journal.ParseFailures);
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

    private static async Task
        WaitUntilAsync(Func<bool> ready, TimeSpan timeout, string message, CancellationToken ct) =>
        await WaitUntilAsync(() => Task.FromResult(ready()), timeout, message, ct);

    private static async Task WaitUntilAsync(Func<Task<bool>> ready,
        TimeSpan timeout,
        string message,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            if (await ready())
                return;
            await Task.Delay(100, ct);
        }

        Assert.Fail(message);
    }

    private sealed record Snapshot(
        long Version,
        DateTimeOffset CheckedAt,
        HashSet<string> ClassNames);

    private sealed record CapturedRead(
        IRequest Request,
        long CapturedAt,
        Snapshot Snapshot);

    private sealed record Read(long StartedAt);

    private sealed record Invocation(
        string Method,
        int Epoch);

    private sealed class StorageJournal
    {
        private readonly string targetId;
        private readonly long connectionVersion;
        private readonly string canary;
        private readonly ConcurrentDictionary<IRequest, Read> reads = new();
        private readonly ConcurrentDictionary<IRequest, bool> finished = new();
        private readonly ConcurrentDictionary<IRequest, bool> failed = new();
        private readonly ConcurrentDictionary<int, bool> coverage = new();
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
        public bool Covered => coverage.TryGetValue(Volatile.Read(ref connection), out var current) && current;
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
                    coverage.Clear();
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
                    reads[request] = new Read(Stopwatch.GetTimestamp());
            };
            page.RequestFinished += (_, request) =>
            {
                if (reads.ContainsKey(request))
                    finished[request] = true;
            };
            page.RequestFailed += (_, request) =>
            {
                if (reads.ContainsKey(request))
                {
                    failed[request] = true;
                    finished[request] = true;
                }
            };
            page.Response += (_, response) =>
            {
                if (IsObservation(response.Request))
                    pending.Enqueue(RecordAsync(response));
            };
            page.WebSocket += (_pageSender, socket) =>
            {
                CheckUrl(socket.Url);
                if (new Uri(socket.Url).AbsolutePath != "/api/v1/realtime/hub")
                    return;
                var id = Interlocked.Increment(ref connection);
                var epoch = 0;
                var closed = 0;
                var invocations = new ConcurrentDictionary<string, Invocation>();
                socket.Close += (_closeSender, _closeEvent) =>
                {
                    Interlocked.Exchange(ref closed, 1);
                    coverage.TryRemove(id, out _);
                };
                socket.FrameSent += (_sentSender, frame) => ParseFrames(frame.Text,
                    message =>
                    {
                        if (Number(message, "type") != 1 || Text(message, "invocationId") is not { } invocation)
                            return;
                        var method = Text(message, "target");
                        if (method is "Subscribe" or "Unsubscribe")
                        {
                            Interlocked.Increment(ref epoch);
                            coverage.TryRemove(id, out _);
                        }

                        if (method is "Subscribe" or "Heartbeat" or "Unsubscribe")
                            invocations[invocation] = new Invocation(method, Volatile.Read(ref epoch));
                    });
                socket.FrameReceived += (_receivedSender, frame) => ParseFrames(frame.Text,
                    message =>
                    {
                        if (Volatile.Read(ref closed) != 0)
                            return;
                        if (Text(message, "target") == "SessionRevoked")
                        {
                            coverage.TryRemove(id, out _);
                            return;
                        }

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

                        if (Number(message, "type") != 3 || Text(message, "invocationId") is not { } key ||
                            !invocations.TryRemove(key, out var invocation) ||
                            invocation.Method is not ("Subscribe" or "Heartbeat") ||
                            invocation.Epoch != Volatile.Read(ref epoch))
                            return;
                        coverage[id] = message.TryGetProperty("result", out var result) &&
                                       result.ValueKind == JsonValueKind.Array &&
                                       result.EnumerateArray().Any(item =>
                                           Text(item, "topic") == "storage-observations" &&
                                           Text(item, "resourceId") == targetId &&
                                           Text(item, "tenantId") is null && item.GetProperty("covered").GetBoolean());
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

        public bool ReadsQuiet(TimeSpan quiet) => !reads.Keys.Any(request => !finished.ContainsKey(request)) &&
                                                  Stopwatch.GetElapsedTime(reads.Values.Select(item => item.StartedAt)
                                                      .DefaultIfEmpty().Max()) >= quiet;

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

        private async Task RecordAsync(IResponse response)
        {
            try
            {
                Assert.True(response.Ok, "A coherent browser observation request returned failure.");
                if (await response.JsonAsync() is not { } value)
                {
                    Interlocked.Increment(ref parseFailures);
                    return;
                }

                var snapshot = Metadata(value);
                Snapshot? previous;
                do
                {
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
