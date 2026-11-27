// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Pole.Testing.Kubernetes;

public sealed class NativeWatchTestHandler(
    Func<HttpRequestMessage, string> list,
    Func<HttpRequestMessage, NativeWatchTestStream> watch
) : DelegatingHandler
{
    public ConcurrentQueue<Uri> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!);
        return Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = NativeWatchTestClient.IsWatch(request.RequestUri!)
                    ? new StreamContent(watch(request))
                    : new StringContent(list(request), Encoding.UTF8, "application/json"),
            });
    }
}
