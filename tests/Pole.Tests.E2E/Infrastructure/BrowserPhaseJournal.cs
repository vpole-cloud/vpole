using System.Collections.Concurrent;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using Pole.Testing;

namespace Pole.Tests.E2E.Infrastructure;

internal sealed class BrowserPhaseJournal : IDisposable
{
    private readonly IPage _page;
    private readonly TestPhaseJournal _journal;
    private readonly ConcurrentDictionary<IRequest, long> _requests = new();
    private long _next;

    public BrowserPhaseJournal(IPage page, TestPhaseJournal journal)
    {
        _page = page;
        _journal = journal;
        page.Request += OnRequest;
        page.Response += OnResponse;
        page.RequestFailed += OnFailed;
        page.RequestFinished += OnFinished;
    }

    private void OnRequest(object? sender, IRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            !uri.AbsolutePath.StartsWith("/api/v1/", StringComparison.Ordinal) || _requests.Count >= 256)
            return;
        var id = Interlocked.Increment(ref _next);
        if (_requests.TryAdd(request, id))
            Record(TestPhase.RequestStarted, request, id);
    }

    private void OnResponse(object? sender, IResponse response)
    {
        if (_requests.TryGetValue(response.Request, out var id))
            Record(TestPhase.ResponseReceived, response.Request, id, response.Status);
    }

    private void OnFailed(object? sender, IRequest request)
    {
        if (_requests.TryRemove(request, out var id))
            Record(request.Failure == "net::ERR_ABORTED" ? TestPhase.RequestCancelled : TestPhase.RequestFailed,
                request, id);
    }

    private void OnFinished(object? sender, IRequest request) => _requests.TryRemove(request, out _);

    private void Record(TestPhase phase, IRequest request, long id, int? status = null)
    {
        var uri = new Uri(request.Url);
        var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = QueryHelpers.ParseQuery(uri.Query);
        _journal.Record(phase,
            resourceId: path.Length > 3 ? path[3] : null,
            instanceUid: query.TryGetValue("instanceUid", out var uid) ? uid.ToString() : null,
            container: query.TryGetValue("container", out var container) ? container.ToString() : null,
            request: id,
            status: status,
            revision: query.TryGetValue("expectedRevision", out var revision) && long.TryParse(revision.ToString(), out var number) ? number : null);
    }

    internal static bool IsRuntimeRequest(IRequest request, string deploymentId, string uid, string container, bool follow) =>
        request.Method == "GET" && IsRuntimeUrl(request.Url, deploymentId, uid, container, follow);

    internal static bool IsRuntimeUrl(string url, string deploymentId, string uid, string container, bool follow)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.AbsolutePath != $"/api/v1/resource-deployments/{Uri.EscapeDataString(deploymentId)}/runtime-logs")
            return false;
        var query = QueryHelpers.ParseQuery(uri.Query);
        return query.TryGetValue("instanceUid", out var actualUid) && actualUid == uid &&
               query.TryGetValue("container", out var actualContainer) && actualContainer == container &&
               query.TryGetValue("follow", out var actualFollow) && bool.TryParse(actualFollow.ToString(), out var value) && value == follow;
    }

    public void Dispose()
    {
        _page.Request -= OnRequest;
        _page.Response -= OnResponse;
        _page.RequestFailed -= OnFailed;
        _page.RequestFinished -= OnFinished;
        _requests.Clear();
        _journal.Record(TestPhase.Cleanup);
    }
}
