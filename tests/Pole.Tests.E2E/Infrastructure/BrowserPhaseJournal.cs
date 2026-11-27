// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;
using Pole.Testing;

namespace Pole.Tests.E2E.Infrastructure;

internal sealed class BrowserPhaseJournal : IDisposable
{
    private readonly TestPhaseJournal _journal;
    private readonly IPage _page;
    private readonly ConcurrentDictionary<IRequest, long> _requests = new();
    private int _disposed;

    public BrowserPhaseJournal(IPage page, TestPhaseJournal journal)
    {
        _page = page;
        _journal = journal;
        page.Request += OnRequest;
        page.Response += OnResponse;
        page.RequestFailed += OnFailed;
        page.RequestFinished += OnFinished;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _page.Request -= OnRequest;
        _page.Response -= OnResponse;
        _page.RequestFailed -= OnFailed;
        _page.RequestFinished -= OnFinished;
        _requests.Clear();
        _journal.Record(TestPhase.Cleanup);
    }

    private void OnRequest(object? sender, IRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            !uri.AbsolutePath.StartsWith("/api/v1/", StringComparison.Ordinal) || _requests.Count >= 256)
            return;
        var id = _journal.AllocateRequestId();
        if (_requests.TryAdd(request, id))
        {
            if (request.Method is "POST" or "PUT" or "PATCH" or "DELETE")
                Record(TestPhase.Action, request, id);
            Record(TestPhase.RequestStarted, request, id);
        }
    }

    private void OnResponse(object? sender, IResponse response)
    {
        if (_requests.TryGetValue(response.Request, out var id))
        {
            Record(TestPhase.ResponseReceived, response.Request, id, response.Status);
            if (IsRuntimeLogs(response.Request))
                Record(
                    response.Status is >= 200 and < 300 ? TestPhase.StreamOpening : TestPhase.StreamFailed,
                    response.Request,
                    id,
                    response.Status);
        }
    }

    private void OnFailed(object? sender, IRequest request)
    {
        if (!_requests.TryRemove(request, out var id))
            return;
        var cancelled = request.Failure == "net::ERR_ABORTED";
        Record(cancelled ? TestPhase.RequestCancelled : TestPhase.RequestFailed, request, id);
        if (IsRuntimeLogs(request))
            Record(cancelled ? TestPhase.StreamEnded : TestPhase.StreamFailed, request, id);
    }

    private void OnFinished(object? sender, IRequest request)
    {
        if (_requests.TryRemove(request, out var id) && IsRuntimeLogs(request))
            Record(TestPhase.StreamEnded, request, id);
    }

    private static bool IsRuntimeLogs(IRequest request) => Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) &&
        uri.AbsolutePath.EndsWith("/runtime-logs", StringComparison.Ordinal);

    private void Record(
        TestPhase phase,
        IRequest request,
        long id,
        int? status = null)
    {
        PhaseRequestEvidence.Record(_journal, phase, new Uri(request.Url), id, status);
    }

    internal static bool IsRuntimeRequest(
        IRequest request,
        string deploymentId,
        string uid,
        string container,
        bool follow) => request.Method == "GET" && IsRuntimeUrl(request.Url, deploymentId, uid, container, follow);

    internal static bool IsRuntimeUrl(
        string url,
        string deploymentId,
        string uid,
        string container,
        bool follow)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.AbsolutePath !=
            $"/api/v1/resource-deployments/{Uri.EscapeDataString(deploymentId)}/runtime-logs")
            return false;
        var query = QueryHelpers.ParseQuery(uri.Query);
        return query.TryGetValue("instanceUid", out var actualUid) && actualUid == uid &&
            query.TryGetValue("container", out var actualContainer) && actualContainer == container &&
            query.TryGetValue("follow", out var actualFollow) &&
            bool.TryParse(actualFollow.ToString(), out var value) && value == follow;
    }
}
