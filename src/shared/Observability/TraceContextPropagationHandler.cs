// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Observability;

public sealed class TraceContextPropagationHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!request.Headers.Contains(TraceHeaders.CorrelationId))
            request.Headers.TryAddWithoutValidation(TraceHeaders.CorrelationId, TraceContext.CurrentCorrelationId);

        return base.SendAsync(request, cancellationToken);
    }
}
