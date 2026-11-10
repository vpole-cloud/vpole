using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Playwright;
using Pole.Testing;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class AutomaticPhaseEvidenceTests
{
    [Fact]
    public async Task Ambient_activation_restores_nested_context_and_flows_into_async_work()
    {
        using var directory = new EvidenceDirectory();
        var before = TestPhaseJournal.Current;
        var outer = new TestPhaseJournal("outer", directory.Path);
        var inner = new TestPhaseJournal("inner", directory.Path);
        using (outer.Activate())
        {
            Assert.Same(outer, TestPhaseJournal.Current);
            using (inner.Activate())
            {
                Assert.Same(inner, await Task.Run(() => TestPhaseJournal.Current));
            }

            Assert.Same(outer, TestPhaseJournal.Current);
        }

        Assert.Same(before, TestPhaseJournal.Current);
    }

    [Fact]
    public async Task Run_creates_an_ambient_journal_and_restores_the_callers_context()
    {
        var before = TestPhaseJournal.Current;
        TestPhaseJournal? observed = null;
        var resources = new TestResourceScope("automatic scenario journal");
        await resources.RunAsync(async () =>
        {
            await Task.Yield();
            observed = TestPhaseJournal.Current;
            Assert.NotNull(observed);
        });
        Assert.NotNull(observed);
        Assert.Same(before, TestPhaseJournal.Current);
    }

    [Fact]
    public async Task Journal_creation_failure_cannot_replace_body_or_cleanup_failures()
    {
        using var directory = new EvidenceDirectory();
        var blocked = Path.Combine(directory.Path, "existing-file");
        File.WriteAllText(blocked, "not a directory");
        var journal = new TestPhaseJournal("unavailable", blocked);
        Assert.False(journal.Record(TestPhase.Started));
        using var activation = journal.Activate();
        var bodyFailure = new InvalidOperationException("body sentinel");
        var cleanupFailure = new InvalidOperationException("cleanup sentinel");
        var cleanupAttempts = 0;
        var resources = new TestResourceScope("unavailable diagnostics");
        resources.Defer(
            "remaining cleanup",
            _ =>
            {
                cleanupAttempts++;
                return Task.CompletedTask;
            });
        resources.Defer(
            "failing cleanup",
            _ =>
            {
                cleanupAttempts++;
                throw cleanupFailure;
            });

        var failure = await Assert.ThrowsAsync<AggregateException>(() => resources.RunAsync(() => throw bodyFailure));

        Assert.Contains(bodyFailure, failure.Flatten().InnerExceptions);
        Assert.Contains(
            failure.Flatten().InnerExceptions,
            item => ReferenceEquals(item.InnerException, cleanupFailure));
        Assert.Equal(2, cleanupAttempts);
        Assert.Same(journal, TestPhaseJournal.Current);
    }

    [Fact]
    public async Task Run_records_cleanup_and_terminal_failure_without_masking_the_body()
    {
        using var directory = new EvidenceDirectory();
        var journal = new TestPhaseJournal("body failure", directory.Path);
        using var activation = journal.Activate();
        var expected = new InvalidOperationException("private exception canary");
        var resources = new TestResourceScope("owned resources");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resources.RunAsync(() => throw expected));
        Assert.Same(expected, actual);
        Assert.Equal(new[] { "Started", "Failed", "Cleanup" }, Phases(journal));
        Assert.DoesNotContain("private exception canary", File.ReadAllText(journal.Path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_share_unique_ids_and_never_record_headers_payloads_or_unknown_query_values()
    {
        using var directory = new EvidenceDirectory();
        var journal = new TestPhaseJournal("request evidence", directory.Path);
        using var activation = journal.Activate();
        using var api = Client((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("response-payload-canary"),
            }));
        var browserRequestId = journal.AllocateRequestId();
        journal.Record(TestPhase.RequestStarted, request: browserRequestId);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/resource-deployments/resource-canary/runtime-logs?instanceUid=uid-canary&container=container-canary&expectedRevision=7&token=query-token-canary");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "header-token-canary");
        request.Headers.Add("Cookie", "cookie-canary");
        request.Content = new StringContent("request-payload-canary");
        using var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var output = File.ReadAllText(journal.Path);
        foreach (var canary in new[]
                 {
                     "resource-canary", "uid-canary", "container-canary", "query-token-canary",
                     "header-token-canary", "cookie-canary", "request-payload-canary", "response-payload-canary",
                 })
            Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
        var records = Read(journal);
        try
        {
            var start = records.Single(item => item.RootElement.GetProperty("phase").GetString() == "RequestStarted" &&
                item.RootElement.GetProperty("request").GetInt64() != browserRequestId);
            var end = records.Single(item => item.RootElement.GetProperty("phase").GetString() == "ResponseReceived");
            Assert.Equal(2, start.RootElement.GetProperty("request").GetInt64());
            Assert.Equal(2, end.RootElement.GetProperty("request").GetInt64());
            Assert.Equal(403, end.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(7, start.RootElement.GetProperty("revision").GetInt64());
        }
        finally
        {
            foreach (var record in records)
                record.Dispose();
        }
    }

    [Fact]
    public async Task Transport_failures_preserve_the_original_exception_and_record_a_typed_outcome()
    {
        using var directory = new EvidenceDirectory();
        var journal = new TestPhaseJournal("transport", directory.Path);
        using var activation = journal.Activate();
        var expected = new HttpRequestException("private transport detail canary");
        using var api = Client((_, _) => throw expected);
        var actual = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetAsync("/api/v1/web-services/owned"));
        Assert.Same(expected, actual);
        Assert.Equal(new[] { "Started", "RequestStarted", "RequestFailed" }, Phases(journal));
        Assert.DoesNotContain(
            "private transport detail canary",
            File.ReadAllText(journal.Path),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_requests_are_distinct_from_transport_failures()
    {
        using var directory = new EvidenceDirectory();
        var journal = new TestPhaseJournal("cancellation", directory.Path);
        using var activation = journal.Activate();
        using var cancellation = new CancellationTokenSource();
        using var api = Client(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.GetAsync(
            "/api/v1/web-services/owned",
            cancellation.Token));
        Assert.Equal(new[] { "Started", "RequestStarted", "RequestCancelled" }, Phases(journal));
    }

    [Fact]
    public async Task Successful_body_and_cleanup_record_completion_once()
    {
        using var directory = new EvidenceDirectory();
        var journal = new TestPhaseJournal("successful", directory.Path);
        using var activation = journal.Activate();
        var resources = new TestResourceScope("owned resources");
        await resources.RunAsync(() => Task.CompletedTask);
        await resources.DisposeAsync();
        Assert.Equal(new[] { "Started", "BodyCompleted", "Cleanup", "Completed" }, Phases(journal));
    }

    [Fact]
    public async Task Browser_factory_attaches_safe_evidence_and_detaches_on_close()
    {
        using var directory = new EvidenceDirectory();
        var journal = new TestPhaseJournal("browser", directory.Path);
        using var activation = journal.Activate();
        var page = DispatchProxy.Create<IPage, EventProxy>();
        var pageProxy = (EventProxy)page;
        var context = DispatchProxy.Create<IBrowserContext, EventProxy>();
        ((EventProxy)context).Results["NewPageAsync"] = Task.FromResult(page);
        Assert.Same(page, await BrowserEvidence.NewPageAsync(context));
        var request = DispatchProxy.Create<IRequest, EventProxy>();
        var requestProxy = (EventProxy)request;
        requestProxy.Results["get_Url"]
            = "https://localhost/api/v1/resource-deployments/browser-resource-canary/runtime-logs?instanceUid=browser-uid-canary&container=browser-container-canary&token=unknown-query-canary";
        requestProxy.Results["get_Method"] = "GET";
        requestProxy.Results["get_Failure"] = "net::ERR_ABORTED";
        var response = DispatchProxy.Create<IResponse, EventProxy>();
        var responseProxy = (EventProxy)response;
        responseProxy.Results["get_Request"] = request;
        responseProxy.Results["get_Status"] = 200;
        pageProxy.Raise("Request", page, request);
        pageProxy.Raise("Response", page, response);
        pageProxy.Raise("RequestFailed", page, request);
        pageProxy.Raise("Close", page, page);
        pageProxy.Raise("Close", page, page);
        pageProxy.Raise("Request", page, request);
        Assert.Equal(
            new[]
            {
                "Started", "RequestStarted", "ResponseReceived", "StreamOpening", "RequestCancelled", "StreamEnded",
                "Cleanup",
            },
            Phases(journal));
        var output = File.ReadAllText(journal.Path);
        foreach (var canary in new[]
                 {
                     "browser-resource-canary", "browser-uid-canary", "browser-container-canary", "unknown-query-canary",
                 })
            Assert.DoesNotContain(canary, output, StringComparison.Ordinal);
    }

    private static E2eApiClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new HttpClient(new Handler(send)) { BaseAddress = new Uri("https://evidence.invalid") });

    private static JsonDocument[] Read(TestPhaseJournal journal) =>
        File.ReadAllLines(journal.Path).Select(line => JsonDocument.Parse(line)).ToArray();

    private static string[] Phases(TestPhaseJournal journal)
    {
        var records = Read(journal);
        try
        {
            return records.Select(item => item.RootElement.GetProperty("phase").GetString()!).ToArray();
        }
        finally
        {
            foreach (var record in records)
                record.Dispose();
        }
    }

    public class EventProxy : DispatchProxy
    {
        private readonly Dictionary<string, Delegate?> _events = [];
        public Dictionary<string, object?> Results { get; } = [];

        public void Raise(
            string name,
            object sender,
            object value)
        {
            if (_events.TryGetValue(name, out var handler))
                handler?.DynamicInvoke(sender, value);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            if (name.StartsWith("add_", StringComparison.Ordinal))
            {
                _events.TryGetValue(name[4..], out var existing);
                _events[name[4..]] = Delegate.Combine(existing, (Delegate)args![0]!);
                return null;
            }

            if (name.StartsWith("remove_", StringComparison.Ordinal))
            {
                _events.TryGetValue(name[7..], out var existing);
                _events[name[7..]] = Delegate.Remove(existing, (Delegate)args![0]!);
                return null;
            }

            if (Results.TryGetValue(name, out var result))
                return result;
            throw new InvalidOperationException("Evidence accessed an unexpected browser member.");
        }
    }

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class EvidenceDirectory : IDisposable
    {
        public EvidenceDirectory() => Directory.CreateDirectory(Path);

        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pole-evidence-" + Guid.NewGuid().ToString("N"));

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
