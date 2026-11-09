using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Pole.Tests.E2E.Infrastructure;

internal sealed record BootstrapMetadata(
    long Timestamp,
    DateTimeOffset ExpiresAtUtc);

internal sealed record ChangeMetadata(
    long Timestamp,
    string ResourceId,
    long Revision);

internal sealed class RealtimeBrowserJournal
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

    public RealtimeBrowserJournal(IPage page, string tenantId)
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
